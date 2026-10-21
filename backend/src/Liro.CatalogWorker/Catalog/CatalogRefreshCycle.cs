using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;

namespace Liro.CatalogWorker.Catalog;

public static class CatalogRefreshCycle
{
    public static async Task<int> RunAsync(IServiceScopeFactory scopes, CatalogWorkerOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ICatalogPriorityQueue>();
        var entries = await queue.GetDueAsync(DateTime.UtcNow, options.DetailsBatchSize, cancellationToken);
        if (entries.Count > 0)
        {
            using var priority = new RobloxRequestPriority();
            var results = await scope.ServiceProvider.GetRequiredService<IRobloxCatalogClient>().GetItemsAsync(entries.Select(x => x.Key).ToArray(), cancellationToken);
            foreach (var entry in entries)
            {
                using var itemScope = scopes.CreateScope();
                var processor = itemScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>();
                if (!await processor.CanProcessAsync(entry.Key, false, cancellationToken))
                    continue;
                var result = results.GetValueOrDefault(entry.Key) ?? new RobloxCatalogResult(null, "Item omitted from priority response.");
                var outcome = await processor.ProcessResultAsync(entry.Key, result, false, cancellationToken, refreshThumbnail: false);
                if (outcome.Failed)
                    continue; // Persisted retry backoff applies to this queue too.
                await queue.CompleteAsync(entry, cancellationToken);
                logger.LogInformation("Priority catalog item confirmed. Item: {Key}, DetectionToSaveSeconds: {Seconds}",
                    entry.Key, Math.Max(0, (DateTime.UtcNow - entry.DetectedAtUtc).TotalSeconds));
            }
        }

        // Priority confirmations stay active even when the historical catalog sweep is disabled.
        if (!options.HistoricalRefreshEnabled)
            return entries.Count;

        // One ordinary details batch per turn prevents starvation without delaying the next priority check by 50 thumbnails.
        var now = DateTime.UtcNow;
        var retries = await scope.ServiceProvider.GetRequiredService<ICatalogImportFailureStore>().GetDueItemsAsync(now, options.DetailsBatchSize, cancellationToken);
        var stale = await scope.ServiceProvider.GetRequiredService<IItemRepository>().GetStaleItemsAsync(now, options.DetailsBatchSize, cancellationToken);
        var priorityKeys = entries.Select(x => x.Key).ToHashSet();
        var ordinary = retries.Take(Math.Max(1, options.DetailsBatchSize / 2)).Concat(stale).Concat(retries)
            .Distinct().Where(x => !priorityKeys.Contains(x)).Take(options.DetailsBatchSize).ToArray();
        await CatalogBatchProcessor.ProcessAsync(scopes, ordinary, false, options, cancellationToken);
        return entries.Count + ordinary.Length;
    }
}
