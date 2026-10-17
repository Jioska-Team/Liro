using Liro.Domain.Catalog.Models;
namespace Liro.Application.RobloxIntegration.Models;

public sealed record RobloxCatalogDiscoveryPage(IReadOnlyList<long> AssetIds, string? NextPageCursor)
{
    public int ReturnedCount { get; init; } = AssetIds.Count;
    public IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult> Details { get; init; } = new Dictionary<CatalogItemKey, RobloxCatalogResult>();
    public IReadOnlyList<CatalogItemKey> LimitedItems { get; init; } = [];
    // Discovery hints only. Each component must be confirmed independently before import.
    public IReadOnlyList<CatalogItemKey> BundledAssets { get; init; } = [];
    public IReadOnlyList<CatalogItemKey> Items { get; init; } = AssetIds.Select(id => new CatalogItemKey(id)).ToArray();
}


