using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Liro.Infrastructure.Persistence;
// StartAsync finishes before subsequent hosted services and the HTTP server start.
public sealed class DatabaseInitializationService(IServiceScopeFactory scopes, IHostEnvironment environment, ILogger<DatabaseInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LiroDbContext>();
                if (environment.IsDevelopment())
                {
                    await db.Database.MigrateAsync(cancellationToken);
                }
                else if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                {
                    throw new InvalidOperationException("Database migrations are pending. Apply the backend migrations before starting services.");
                }

                var policy = scope.ServiceProvider.GetRequiredService<CatalogRefreshPolicy>();
                var interval = policy.Interval;
                var rescheduled = await db.Items
                    .Where(policy.GetLegacyScheduleFilter())
                    .Where(item => !db.Set<CatalogImportFailure>().Any(failure => failure.AssetId == item.RobloxAssetId))
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextRefreshAt, item => item.LastCheckedAt!.Value.AddMinutes(interval.TotalMinutes)), cancellationToken);
                logger.LogInformation("Database schema is ready. Rescheduled {Count} legacy catalog checks; refresh interval {Interval}.", rescheduled, interval);
                return;
            }
            catch (NpgsqlException exception) when (exception.IsTransient && attempt < 12)
            {
                logger.LogWarning(exception, "Database is not ready, attempt {Attempt}/12.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
