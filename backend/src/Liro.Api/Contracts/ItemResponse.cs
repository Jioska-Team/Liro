using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Api.Contracts;

public sealed record ItemResponse(
    Guid Id,
    long? RobloxAssetId,
    Guid? CollectibleItemId,
    string Name,
    ItemMarketStatus MarketStatus,
    string? ThumbnailUrl,
    DateTime? ThumbnailUpdatedAt,
    DateTime FirstSeenAt,
    DateTime LastUpdatedAt,
    DateTime? LastCheckedAt)
{
    public long RobloxItemId
    {
        get; init;
    }
    public string ItemType { get; init; } = "Asset";
    public bool IsBodyPart
    {
        get; init;
    }
    public bool IsCatalogVisible
    {
        get; init;
    }
    public int? BundleTypeId
    {
        get; init;
    }
    public string MarketplaceUrl { get; init; } = string.Empty;
    public string? Description
    {
        get; init;
    }
    public long? CreatorId
    {
        get; init;
    }
    public string? CreatorType
    {
        get; init;
    }
    public string? CreatorName
    {
        get; init;
    }
    public int? AssetTypeId
    {
        get; init;
    }
    public IReadOnlyList<string> Restrictions { get; init; } = [];
    public DateTime? RobloxCreatedAtUtc
    {
        get; init;
    }
    public DateTime? RobloxUpdatedAtUtc
    {
        get; init;
    }
    public ItemMarketResponse? Market
    {
        get; init;
    }

    public long? OriginalPriceRobux { get; init; }
    public long? CurrentPriceRobux { get; init; }
    public decimal? RapRobux { get; init; }

    public static ItemResponse From(Item item) => From(item, null);

    public static ItemResponse From(Item item, decimal? rap)
    {
        var response = new ItemResponse(
        item.Id,
        item.ItemType == CatalogItemType.Asset ? item.RobloxAssetId : null,
        item.CollectibleItemId,
        item.Name,
        item.MarketStatus,
        item.ThumbnailUrl,
        item.ThumbnailUpdatedAt,
        item.FirstSeenAt,
        item.LastUpdatedAt,
        item.LastCheckedAt)
        {
            RobloxItemId = item.RobloxAssetId,
            ItemType = item.ItemType.ToString(),
            IsBodyPart = Liro.Domain.Catalog.Models.CatalogVisibility.IsBodyPart(item),
            IsCatalogVisible = !Liro.Domain.Catalog.Models.CatalogVisibility.IsBodyPart(item),
            BundleTypeId = item.BundleTypeId,
            Description = item.Description,
            MarketplaceUrl = item.MarketplaceUrl,
            CreatorId = item.CreatorId,
            CreatorType = item.CreatorType,
            CreatorName = item.CreatorName,
            AssetTypeId = item.AssetTypeId,
            Restrictions = item.Restrictions.ToArray(),
            RobloxCreatedAtUtc = item.RobloxCreatedAtUtc,
            RobloxUpdatedAtUtc = item.RobloxUpdatedAtUtc,
            // A live non-Limited listing supplies its base price even without a discount.
            // Limited listing prices are not evidence of their historical original price.
            OriginalPriceRobux = item.MarketState?.Data.PriceBeforeDiscountRobux
                ?? (item.MarketStatus is not (ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
                    && item.MarketState?.Data.IsOffSale == false ? item.MarketState.Data.PrimaryPriceRobux : null),
            CurrentPriceRobux = item.MarketState?.Data.GetAvailablePrice(),
            RapRobux = item.MarketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique ? rap : null,
            Market = item.MarketState is { } state ? ItemMarketResponse.From(state.Data, state.ObservedAtUtc, state.Source) : null
        };
        return response;
    }
}


