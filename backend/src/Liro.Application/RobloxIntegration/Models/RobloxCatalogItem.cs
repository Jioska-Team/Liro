using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;

namespace Liro.Application.RobloxIntegration.Models;

public sealed record RobloxCatalogItem(long AssetId, string Name, Guid? CollectibleItemId, ItemMarketStatus MarketStatus)
{
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
