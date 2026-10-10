using System.Net;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;

namespace Liro.Workers.Catalog;

public sealed class CatalogBootstrapWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogBootstrapWorker> logger, IOptions<CatalogWorkerOptions> workerOptions, IOptions<RobloxCatalogOptions> catalogOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        if (!options.BootstrapEnabled)
        {
            return;
        }

        var key = catalogOptions.Value.ScopeKey;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var checkpoints = scope.ServiceProvider.GetRequiredService<ICatalogCheckpointStore>();
                var checkpoint = await checkpoints.LoadAsync(key, stoppingToken);
                if (checkpoint.Completed)
                {
                    return;
                }

                var discovery = scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>();
                Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage page;
                try
                {
                    page = await discovery.GetCatalogPageAsync(checkpoint.Cursor, stoppingToken);
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest && checkpoint.Cursor is not null)
                {
                    logger.LogWarning("Catalog cursor expired; restarting this configured scope.");
                    await checkpoints.SaveAsync(key, new(null, false), stoppingToken);
                    await Task.Delay(options.RetryDelay, stoppingToken);
                    continue;
                }

                foreach (var assetId in page.AssetIds)
                {
                    using var itemScope = scopeFactory.CreateScope();
                    await itemScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>().ProcessAsync(assetId, true, stoppingToken);
                    await Task.Delay(options.ItemDelay, stoppingToken);
                }

                var completed = string.IsNullOrWhiteSpace(page.NextPageCursor);
                await checkpoints.SaveAsync(key, new(page.NextPageCursor, completed), stoppingToken);
                logger.LogInformation("Catalog bootstrap page saved. Items: {Count}, Completed: {Completed}", page.AssetIds.Count, completed);
                if (completed)
                {
                    return;
                }

                await Task.Delay(options.PageDelay, stoppingToken);
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
                logger.LogWarning(
                    exception,
                    "Catalog bootstrap temporarily unavailable; retrying from the saved checkpoint after {Delay}.",
                    options.RetryDelay);
                await Task.Delay(options.RetryDelay, stoppingToken);
            }
        }
    }
}
