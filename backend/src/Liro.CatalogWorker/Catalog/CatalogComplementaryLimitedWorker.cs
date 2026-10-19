using System.Net;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;

namespace Liro.CatalogWorker.Catalog;

// Background discovery, separate from the fast head poll and known-ID confirmation.
public sealed class CatalogComplementaryLimitedWorker(IServiceScopeFactory scopes, IOptions<CatalogWorkerOptions> settings,
    IOptions<RobloxCatalogOptions> catalog, ILogger<CatalogComplementaryLimitedWorker> logger) : BackgroundService
{
    public static IReadOnlyList<CatalogSearchPartition> Partitions()
    {
        var roots = new List<CatalogSearchPartition>();
        foreach (var sort in new[] { 0, 1, 2, 4, 5 }) roots.Add(new(CatalogSearchScope.Collectibles) { SortType = sort });
        foreach (var type in new[] { 8, 19, 41, 42, 43, 44, 45, 46, 47, 67, 72 })
            roots.Add(new(CatalogSearchScope.Collectibles) { AssetTypeId = type, SortType = 3 });
        foreach (var type in new[] { 3, 4 }) roots.Add(new(CatalogSearchScope.Collectibles) { BundleTypeId = type, SortType = 3 });
        // Hats alone saturate; complementary orders retain off-sale candidates without price bounds.
        foreach (var sort in new[] { 0, 1, 2, 4, 5 }) roots.Add(new(CatalogSearchScope.Collectibles) { AssetTypeId = 8, SortType = sort });
        return roots;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.Value.ComplementaryLimitedSearchEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await RunPassAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RobloxRateLimitedException ex) { await CatalogWorkerDelay.WaitForRateLimitAsync(ex, settings.Value.RetryDelay, logger, ct); continue; }
            catch (Exception ex) { logger.LogWarning(ex, "Complementary limited pass failed; independent cursors retained."); }
            await Task.Delay(settings.Value.ComplementaryLimitedInterval, ct);
        }
    }

    public async Task RunPassAsync(CancellationToken ct)
    {
        using var operation = RobloxRequestPriority.Operation("búsquedas complementarias");
        var pending = new Queue<CatalogSearchPartition>(Partitions());
        var seen = new Dictionary<string, HashSet<string>>();
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var pages = 0;
        while (pending.TryDequeue(out var partition))
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ICatalogCheckpointStore>();
            var key = catalog.Value.ScopeKey + ":limited-complement:v1:" + partition.Key;
            var checkpoint = await store.LoadAsync(key, ct);
            if (!seen.TryGetValue(key, out var cursors)) seen[key] = cursors = new();
            var cursor = checkpoint.Cursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("Complementary search returned a cyclic cursor.");
            RobloxCatalogDiscoveryPage page;
            try { page = await CatalogWorkerDelay.ReadPendingPageAsync(token => scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>().GetPartitionPageAsync(partition, cursor, token), logger, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && cursor is not null)
            {
                await store.SaveAsync(key, new(null, false), ct);
                logger.LogWarning("Complementary limited cursor expired for {Partition}; retained for next pass.", partition.Key);
                continue;
            }
            var fallback = page.LimitedItems.Where(x => !page.Details.TryGetValue(x, out var r) || r.Item is null || r.Error is not null).ToArray();
            await scope.ServiceProvider.GetRequiredService<ICatalogPriorityQueue>().EnqueueAsync(fallback, DateTime.UtcNow, ct);
            var outcome = await CatalogSearchPageProcessor.ProcessAsync(scopes, page, false, settings.Value, ct, fetchFallback: false);
            var next = string.IsNullOrWhiteSpace(page.NextPageCursor) ? null : page.NextPageCursor;
            await store.SaveAsync(key, new(next, false)
            {
                ItemsSeen = checkpoint.ItemsSeen + page.ReturnedCount,
                ItemsAdded = checkpoint.ItemsAdded + outcome.Added,
                ItemFailures = checkpoint.ItemFailures + outcome.Failed,
                PagesProcessed = checkpoint.PagesProcessed + 1,
                CoverageStatus = next is null ? "SearchExhausted" : "Running"
            }, ct);
            pages++;
            if (next is not null) pending.Enqueue(partition);
        }
        logger.LogInformation("Complementary limited pass completed. Partitions: {Partitions}, Pages: {Pages}, ElapsedSeconds: {Seconds}",
            Partitions().Count, pages, System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds);
    }
}
