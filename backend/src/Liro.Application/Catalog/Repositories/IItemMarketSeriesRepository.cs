namespace Liro.Application.Catalog.Repositories;

public interface IItemMarketSeriesRepository
{
    Task<DailySalesPage> GetDailyAsync(Guid itemId, DateOnly from, DateOnly to, DateOnly? after, int limit, CancellationToken cancellationToken);
    Task<RapHistoryPage> GetRapAsync(Guid itemId, DateOnly from, DateOnly to, DateTimeOffset? after, int limit, CancellationToken cancellationToken);
}

public sealed record DailySalesPoint(DateOnly DateUtc, decimal? AveragePriceRobux, decimal? SalesVolume, DateTime UpdatedAtUtc);
public sealed record RapHistoryPoint(DateTime ObservedAtUtc, decimal Rap);
public sealed record DailySalesPage(DateOnly FromUtc, DateOnly ToUtcExclusive, IReadOnlyList<DailySalesPoint> Points, DateOnly? NextAfter);
public sealed record RapHistoryPage(DateOnly FromUtc, DateOnly ToUtcExclusive, IReadOnlyList<RapHistoryPoint> Points, DateTimeOffset? NextAfter);

public static class MarketSeriesRange
{
    public static bool IsValid(DateOnly from, DateOnly to, int limit) =>
        limit is >= 1 and <= 500 && to.DayNumber > from.DayNumber && to.DayNumber - from.DayNumber <= 366;

    public static DateTime Utc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
