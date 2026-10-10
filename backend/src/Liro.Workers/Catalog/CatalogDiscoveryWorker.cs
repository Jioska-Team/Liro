using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Options;

namespace Liro.Workers.Catalog;

public sealed class CatalogDiscoveryWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogDiscoveryWorker> logger, IOptions<CatalogWorkerOptions> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (!options.DiscoveryEnabled)
        {
            return;
        }

        await Task.Delay(options.CycleInterval, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? cursor = null;
                for (var pageNumber = 0; pageNumber < options.DiscoveryPages; pageNumber++)
                {
                    using var scope = scopeFactory.CreateScope();
                    var page = await scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>().GetRecentlyUpdatedPageAsync(cursor, stoppingToken);
                    foreach (var id in page.AssetIds)
                    {
                        using var itemScope = scopeFactory.CreateScope();
                        await itemScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>().ProcessAsync(id, true, stoppingToken);
                        await Task.Delay(options.ItemDelay, stoppingToken);
                    }

                    cursor = page.NextPageCursor;
                    if (string.IsNullOrWhiteSpace(cursor))
                    {
                        break;
                    }

                    await Task.Delay(options.PageDelay, stoppingToken);
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
                logger.LogWarning(exception, "Discovery cycle failed; retrying on the next cycle.");
            }

            await Task.Delay(options.CycleInterval, stoppingToken);
        }
    }
}
