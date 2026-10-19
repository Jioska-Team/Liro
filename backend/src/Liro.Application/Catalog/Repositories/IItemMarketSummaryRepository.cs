namespace Liro.Application.Catalog.Repositories;

public interface IItemMarketSummaryRepository
{
    Task<ItemMarketSummary?> GetAsync(Guid itemId, CancellationToken cancellationToken);
}

public sealed record ItemMarketSummary
{
    public Guid ItemId { get; init; }
    public long RobloxItemId { get; init; }
    public string ItemType { get; init; } = "";
    public string Name { get; init; } = "";
    public string MarketplaceUrl { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public long? LowestResalePriceRobux { get; init; }
    public bool? HasResellers { get; init; }
    public DateTime? PriceObservedAtUtc { get; init; }
    public string? PriceSource { get; init; }
    public decimal? RecentAveragePrice { get; init; }
    public DateTime? RapObservedAtUtc { get; init; }
    public string? Activity { get; init; }
    public decimal? DailySales { get; init; }
}
