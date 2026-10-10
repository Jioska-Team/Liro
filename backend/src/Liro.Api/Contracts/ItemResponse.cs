using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Api.Contracts;

public sealed record ItemResponse(
    Guid Id,
    long RobloxAssetId,
    Guid? CollectibleItemId,
    string Name,
    ItemMarketStatus MarketStatus,
    string? ThumbnailUrl,
    DateTime? ThumbnailUpdatedAt,
    DateTime FirstSeenAt,
    DateTime LastUpdatedAt,
    DateTime? LastCheckedAt)
{
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

    public static ItemResponse From(Item item)
    {
        var response = new ItemResponse(
        item.Id,
        item.RobloxAssetId,
        item.CollectibleItemId,
        item.Name,
        item.MarketStatus,
        item.ThumbnailUrl,
        item.ThumbnailUpdatedAt,
        item.FirstSeenAt,
        item.LastUpdatedAt,
        item.LastCheckedAt)
        {
            Description = item.Description,
            MarketplaceUrl = item.MarketplaceUrl,
            CreatorId = item.CreatorId,
            CreatorType = item.CreatorType,
            CreatorName = item.CreatorName,
            AssetTypeId = item.AssetTypeId,
            Restrictions = item.Restrictions.ToArray(),
            RobloxCreatedAtUtc = item.RobloxCreatedAtUtc,
            RobloxUpdatedAtUtc = item.RobloxUpdatedAtUtc,
            Market = item.MarketState is { } state ? ItemMarketResponse.From(state.Data, state.ObservedAtUtc, state.Source) : null
        };
        return response;
    }
}
