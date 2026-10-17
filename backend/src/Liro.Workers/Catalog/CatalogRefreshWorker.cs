using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Options;

namespace Liro.Workers.Catalog;

public sealed class CatalogRefreshWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogRefreshWorker> logger, IOptions<CatalogWorkerOptions> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (!options.RefreshEnabled)
        {
            return;
        }

        var nextMetricsAt = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await CatalogRefreshCycle.RunAsync(scopeFactory, options, logger, stoppingToken);
                if (DateTime.UtcNow >= nextMetricsAt)
                {
                    using var scope = scopeFactory.CreateScope();
                    var now = DateTime.UtcNow;
                    var health = await scope.ServiceProvider.GetRequiredService<ICatalogPriorityQueue>().GetHealthAsync(now, stoppingToken);
                    logger.LogInformation("Catalog queue health. PriorityPending: {PriorityPending}, OldestDetectionUtc: {OldestDetectionUtc}, Overdue: {Overdue}, OldestDueUtc: {OldestDueUtc}",
                        health.Pending, health.OldestDetectedAtUtc, health.Overdue, health.OldestDueAtUtc);
                    nextMetricsAt = now.AddMinutes(1);
                }
                if (processed == 0) await Task.Delay(options.RefreshIdleDelay, stoppingToken);
                continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (RobloxRateLimitedException exception)
            {
                await CatalogWorkerDelay.WaitForRateLimitAsync(exception, options.RetryDelay, logger, stoppingToken);
                continue;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Catalog refresh database cycle failed; next cycle will retry.");
            }

            await Task.Delay(options.RetryDelay, stoppingToken);
        }
    }
}



