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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var now = DateTime.UtcNow;
                var retryIds = await scope.ServiceProvider.GetRequiredService<ICatalogImportFailureStore>().GetDueAsync(now, options.BatchSize, stoppingToken);
                var staleIds = await scope.ServiceProvider.GetRequiredService<IItemRepository>().GetStaleAssetIdsAsync(now, options.BatchSize, stoppingToken);
                foreach (var id in retryIds.Concat(staleIds).Distinct())
                {
                    using var itemScope = scopeFactory.CreateScope();
                    await itemScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>().ProcessAsync(id, false, stoppingToken);
                    await Task.Delay(options.ItemDelay, stoppingToken);
                }
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

            await Task.Delay(options.CycleInterval, stoppingToken);
        }
    }
}
