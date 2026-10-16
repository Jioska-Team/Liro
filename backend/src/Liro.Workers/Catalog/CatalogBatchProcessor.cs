using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Models;

namespace Liro.Workers.Catalog;

public static class CatalogBatchProcessor
{
    public static async Task<(int Added, int Failed)> ProcessAsync(IServiceScopeFactory scopes, IEnumerable<CatalogItemKey> keys, bool onlyNew, CatalogWorkerOptions options, CancellationToken cancellationToken)
    {
        if (options.DetailsBatchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DetailsBatchSize));
        }
        var added = 0;
        var failed = 0;
        var pending = new List<CatalogItemKey>();
        foreach (var key in keys.Distinct())
        {
            using var selection = scopes.CreateScope();
            if (await selection.ServiceProvider.GetRequiredService<CatalogImportProcessor>().CanProcessAsync(key, onlyNew, cancellationToken))
            {
                pending.Add(key);
            }
        }
        // Fill batches after filtering, so mostly-known pages do not produce many tiny requests.
        foreach (var eligible in pending.Chunk(options.DetailsBatchSize))
        {
            using var requestScope = scopes.CreateScope();
            // Request-wide failures (including 429) retain the page checkpoint, not per-item failure budgets.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var results = await requestScope.ServiceProvider.GetRequiredService<IRobloxCatalogClient>().GetItemsAsync(eligible, cancellationToken);
            var logger = requestScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Liro.Workers.Catalog.CatalogBatchProcessor");
            logger?.LogInformation("Catalog details batch fetched. Requested: {Count}, Valid: {Valid}, ElapsedMs: {ElapsedMs}",
                eligible.Length, results.Values.Count(result => result.Item is not null && result.Error is null), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            foreach (var key in eligible)
            {
                using var itemScope = scopes.CreateScope();
                var result = results.GetValueOrDefault(key) ?? new RobloxCatalogResult(null, "Item omitted from catalog response.");
                var outcome = await itemScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>().ProcessResultAsync(key, result, onlyNew, cancellationToken);
                if (outcome.Added)
                {
                    added++;
                }
                if (outcome.Failed)
                {
                    failed++;
                }
            }
            // Pacing is per details batch; all details and thumbnails still pass through the shared HTTP gate.
            await Task.Delay(options.ItemDelay, cancellationToken);
        }
        return (added, failed);
    }
}



