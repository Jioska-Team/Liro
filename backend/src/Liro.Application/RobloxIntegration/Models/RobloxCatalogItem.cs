using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;

namespace Liro.Application.RobloxIntegration.Models;

public sealed record RobloxCatalogItem(long AssetId, string Name, Guid? CollectibleItemId, ItemMarketStatus MarketStatus)
{
    public CatalogItemType ItemType { get; init; } = CatalogItemType.Asset;
    // Null means not supplied; an empty list means a confirmed empty bundle.
    public IReadOnlyList<long>? BundleComponentAssetIds { get; init; }
    public ItemCatalogDetails? Details
    {
        get; init;
    }
    public ItemMarketData? Market
    {
        get; init;
    }
    public DateTime ObservedAtUtc
    {
        get; init;
    }
    public string Source { get; init; } = "RobloxCatalogDetailsV1";
}

