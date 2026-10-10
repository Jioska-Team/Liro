using Liro.Application.Common.Events;
using Liro.Infrastructure.Events;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liro.Workers.Outbox;

public sealed class OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox database cycle failed; retrying without stopping the worker host.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        for (var count = 0; count < 50; count++)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LiroDbContext>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
            var found = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                // The row lock lasts through dispatch + acknowledgement. Other instances skip it.
                var rows = await db.OutboxMessages.FromSqlRaw("""
                    SELECT * FROM "OutboxMessages"
                    WHERE "ProcessedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "NextAttemptAtUtc" <= now()
                    ORDER BY "OccurredAtUtc", "Id"
                    LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(cancellationToken);
                if (rows.Count == 0)
                {
                    return false;
                }

                var message = rows[0];
                try
                {
                    await dispatcher.DispatchAsync([DomainEventSerializer.Deserialize(message.Type, message.Content)], cancellationToken);
                    message.ProcessedAtUtc = DateTime.UtcNow;
                    message.Error = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    message.RecordFailure(exception.Message, DateTime.UtcNow);
                    logger.LogWarning(
                        exception,
                        "Outbox message {MessageId} failed, attempt {Attempt}, quarantined: {Quarantined}.",
                        message.Id,
                        message.Attempts,
                        message.DeadLetteredAtUtc is not null);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            });
            if (!found)
            {
                break;
            }
        }
    }
}
