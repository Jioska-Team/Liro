using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;

namespace Liro.Workers.Catalog;

public static class CatalogRecentWindow
{
    public static async Task<(int Pages, int Observations, bool HitLimit)> ReadAsync(IRobloxCatalogDiscoveryClient client,
        CatalogSearchScope scope, int limit, Func<RobloxCatalogDiscoveryPage, Task> apply, CancellationToken ct)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        string? cursor = null;
        var seen = new HashSet<string>();
        var pages = 0;
        var observations = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await CatalogWorkerDelay.ReadPendingPageAsync(token => client.GetPageAsync(scope, true, cursor, token),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);
            var remaining = limit - observations;
            var keys = page.Items.Take(remaining).ToArray();
            var selected = keys.ToHashSet();
            // The window is for returned catalog items; bundle components remain a separate discovery source.
            var sliced = page with
            {
                AssetIds = keys.Where(x => x.Type == CatalogItemType.Asset).Select(x => x.Id).ToArray(),
                Items = keys,
                Details = page.Details.Where(x => selected.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value),
                LimitedItems = page.LimitedItems.Where(selected.Contains).ToArray(),
                BundledAssets = [],
                ReturnedCount = Math.Min(remaining, page.ReturnedCount)
            };
            await apply(sliced);
            pages++;
            observations += sliced.ReturnedCount;
            cursor = string.IsNullOrWhiteSpace(page.NextPageCursor) ? null : page.NextPageCursor;
            if (observations >= limit) return (pages, observations, true);
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Recent search returned a cyclic cursor.");
        } while (cursor is not null);
        return (pages, observations, false);
    }
}
