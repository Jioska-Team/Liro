using Liro.Application.RobloxIntegration.Models;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Models;

namespace Liro.CatalogWorker.Catalog;

public static class CatalogSearchPageProcessor
{
    public static async Task<(int Added, int Failed)> ProcessAsync(IServiceScopeFactory scopes,
        RobloxCatalogDiscoveryPage page, bool onlyNew, CatalogWorkerOptions options, CancellationToken cancellationToken,
        bool fetchFallback = true)
    {
        var fallback = new List<CatalogItemKey>();
        var added = 0;
        var failed = 0;
        var reused = 0;
        var individual = page.Items.Distinct().ToHashSet();
        var complete = individual.Where(key => page.Details.TryGetValue(key, out var result) && result.Item is not null && result.Error is null)
            .ToDictionary(key => key, key => page.Details[key].Item!);
        using (var tracking = scopes.CreateScope())
        {
            var watchlist = tracking.ServiceProvider.GetService<ICatalogLimitedWatchlist>();
            if (watchlist is not null)
            {
                var knownLimited = complete.Where(x => x.Value.Details?.CreatorId == 1
                    && string.Equals(x.Value.Details.CreatorType, "User", StringComparison.OrdinalIgnoreCase)
                    && x.Value.MarketStatus is Liro.Domain.Catalog.Enums.ItemMarketStatus.Limited or Liro.Domain.Catalog.Enums.ItemMarketStatus.LimitedUnique)
                    .Select(x => x.Key).ToArray();
                await watchlist.TrackAsync(knownLimited, DateTime.UtcNow, cancellationToken);
            }
        }
        // HTTP batches and reused observations can together exceed one database page.
        // Persist only the already fetched data, with an independent scope per chunk.
        foreach (var entries in complete.Chunk(120))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = entries.ToDictionary(x => x.Key, x => x.Value);
            using var batchScope = scopes.CreateScope();
            var writer = batchScope.ServiceProvider.GetService<ICatalogPageWriter>();
            if (writer is not null)
            {
                try
                {
                    var outcome = await writer.WriteAsync(chunk, onlyNew, cancellationToken);
                    added += outcome.Added;
                    reused += chunk.Count - outcome.RetryIndividually.Count;
                    individual.ExceptWith(chunk.Keys);
                    individual.UnionWith(outcome.RetryIndividually);
                }
                catch (CatalogBatchWriteException exception)
                {
                    batchScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("CatalogSearchPageProcessor")
                        .LogWarning(exception, "Catalog batch rolled back; retrying observations in independent scopes.");
                }
            }
        }
        foreach (var key in individual)
        {
            if (!page.Details.TryGetValue(key, out var result) || result.Item is null || result.Error is not null)
            {
                fallback.Add(key);
                continue;
            }
            using var scope = scopes.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<CatalogImportProcessor>();
            if (!await processor.CanProcessAsync(key, onlyNew, cancellationToken)) continue;
            var outcome = await processor.ProcessResultAsync(key, result, onlyNew, cancellationToken, refreshThumbnail: false);
            if (outcome.Added) added++;
            if (outcome.Failed) failed++;
            reused++;
        }
        if (fetchFallback && fallback.Count > 0)
        {
            var outcome = await CatalogBatchProcessor.ProcessAsync(scopes, fallback, onlyNew, options, cancellationToken);
            added += outcome.Added;
            failed += outcome.Failed;
        }
        if (fetchFallback && page.BundledAssets.Count > 0)
        {
            // Discovery only: existing components use the normal refresh workers.
            // The batch importer confirms each asset's creator and respects retry backoff.
            var outcome = await CatalogBatchProcessor.ProcessAsync(scopes, page.BundledAssets.Except(page.Items), true, options, cancellationToken);
            added += outcome.Added;
            failed += outcome.Failed;
            using var componentLogging = scopes.CreateScope();
            componentLogging.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("CatalogSearchPageProcessor")
                .LogInformation("Bundle components processed. Candidates: {Candidates}, Added: {Added}, Failed: {Failed}", page.BundledAssets.Count, outcome.Added, outcome.Failed);
        }
        using var logging = scopes.CreateScope();
        logging.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("CatalogSearchPageProcessor")
            .LogInformation("Catalog search page applied. ReusedDetails: {Reused}, FallbackCandidates: {Fallback}, Added: {Added}, Failed: {Failed}", reused, fallback.Count, added, failed);
        return (added, failed);
    }
}
