namespace Liro.Application.Catalog.Repositories;

public interface IItemMarketSummaryRepository
{
    Task<IReadOnlyDictionary<Guid, decimal?>> GetRapAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    Task<ItemMarketSummary?> GetAsync(Guid itemId, CancellationToken cancellationToken);
}

public sealed record ItemMarketSummary
{
    public int? AssetTypeId { get; init; }
    public long? TotalQuantity { get; init; }
    public long? UnitsAvailableForConsumption { get; init; }
    public long? FavoriteCount { get; init; }
    public int DailySalesWindowDays { get; init; }
    public DateTime DailySalesPeriodStartUtc { get; init; }
    public DateTime DailySalesPeriodEndUtc { get; init; }
    public DateTime? DailySalesObservedAtUtc { get; init; }
    public decimal? RapChange1hPercent { get; init; }
    public decimal? RapChange24hPercent { get; init; }
    public decimal? RapChange7dPercent { get; init; }
    public decimal? RapChange30dPercent { get; init; }
    public double? RapAgeSeconds { get; init; }
    public string? RapSource { get; init; }
    public Guid ItemId
    {
        get; init;
    }
    public long RobloxItemId
    {
        get; init;
    }
    public string ItemType { get; init; } = "";
    public string Name { get; init; } = "";
    public string MarketplaceUrl { get; init; } = "";
    public string? ThumbnailUrl
    {
        get; init;
    }
    public long? LowestResalePriceRobux
    {
        get; init;
    }
    public bool? HasResellers
    {
        get; init;
    }
    public DateTime? PriceObservedAtUtc
    {
        get; init;
    }
    public string? PriceSource
    {
        get; init;
    }
    public decimal? RecentAveragePrice
    {
        get; init;
    }
    public DateTime? RapObservedAtUtc
    {
        get; init;
    }
    public string? Activity
    {
        get; init;
    }
    public decimal? DailySales
    {
        get; init;
    }
}
