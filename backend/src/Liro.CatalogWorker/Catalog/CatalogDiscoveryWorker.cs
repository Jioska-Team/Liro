using System.Net;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
namespace Liro.CatalogWorker.Catalog;

public sealed class CatalogDiscoveryWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogDiscoveryWorker> logger, IOptions<CatalogWorkerOptions> settings, IOptions<RobloxCatalogOptions> catalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (!options.DiscoveryEnabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.BoundedRecentDiscovery)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var result = await CatalogRecentWindow.ReadAsync(scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>(),
                        Liro.Application.RobloxIntegration.Models.CatalogSearchScope.All, options.RecentItemLimit,
                        async page => { await ImportPageAsync(page, options, stoppingToken); }, stoppingToken);
                    logger.LogInformation("Recent item window completed. Limit: {Limit}, Pages: {Pages}, Observations: {Observations}, HitLimit: {HitLimit}",
                        options.RecentItemLimit, result.Pages, result.Observations, result.HitLimit);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (RobloxRateLimitedException exception) { await CatalogWorkerDelay.WaitForRateLimitAsync(exception, options.RetryDelay, logger, stoppingToken); }
                catch (Exception exception) { logger.LogWarning(exception, "Recent item window failed; next cycle starts at the newest page."); }
                await Task.Delay(options.CycleInterval, stoppingToken);
                continue;
            }
            foreach (var searchScope in catalog.Value.SearchScopes.Distinct())
            {
                var key = catalog.Value.ScopeKey + ":recent:" + searchScope;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var checkpoints = scope.ServiceProvider.GetRequiredService<ICatalogCheckpointStore>();
                    var checkpoint = await checkpoints.LoadAsync(key, stoppingToken);
                    var cursor = checkpoint.Cursor;
                    // Poll the newest page even while a saved backlog is still being traversed.
                    if (cursor is not null)
                    {
                        var newest = await scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>().GetPageAsync(searchScope, true, null, stoppingToken);
                        await ImportPageAsync(newest, options, stoppingToken);
                        await Task.Delay(options.PageDelay, stoppingToken);
                    }
                    for (var pageNumber = 0; pageNumber < options.DiscoveryPages; pageNumber++)
                    {
                        Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage page;
                        try
                        {
                            page = await scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>().GetPageAsync(searchScope, true, cursor, stoppingToken);
                        }
                        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest && cursor is not null)
                        {
                            await checkpoints.SaveAsync(key, new(null, false), stoppingToken);
                            logger.LogWarning("Recent catalog cursor expired for {Scope}; next cycle starts at the first page.", searchScope);
                            break;
                        }
                        await ImportPageAsync(page, options, stoppingToken);
                        cursor = string.IsNullOrWhiteSpace(page.NextPageCursor) ? null : page.NextPageCursor;
                        // Persist only after the entire page has been processed or failures queued.
                        await checkpoints.SaveAsync(key, new(cursor, false), stoppingToken);
                        logger.LogInformation("Recent catalog page saved. Scope: {Scope}, Items: {Count}, HasMore: {HasMore}", searchScope, page.Items.Count, cursor is not null);
                        if (cursor is null)
                        {
                            break;
                        }
                        await Task.Delay(options.PageDelay, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (RobloxRateLimitedException exception)
                {
                    await CatalogWorkerDelay.WaitForRateLimitAsync(exception, options.RetryDelay, logger, stoppingToken);
                }
                catch (Exception exception) { logger.LogWarning(exception, "Discovery scope {Scope} failed; saved progress retained.", searchScope); }
            }
            await Task.Delay(options.CycleInterval, stoppingToken);
        }
    }
    private async Task ImportPageAsync(Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage page, CatalogWorkerOptions options, CancellationToken stoppingToken)
    {
        await CatalogSearchPageProcessor.ProcessAsync(scopeFactory, page, false, options, stoppingToken);
    }
}

