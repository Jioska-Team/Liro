using Liro.Application.Catalog.Repositories;
using Liro.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemMarketSeriesRepository(LiroDbContext db) : IItemMarketSeriesRepository
{
    public async Task<DailySalesPage> GetDailyAsync(Guid itemId, DateOnly from, DateOnly to, DateOnly? after, int limit, CancellationToken cancellationToken)
    {
        Validate(from, to, limit);
        var start = MarketSeriesRange.Utc(from);
        var end = MarketSeriesRange.Utc(to);
        var cursor = after ?? DateOnly.MinValue;
        // These are already daily aggregates: never SUM overlapping observations or sources.
        // Pick the latest period of each metric/day, with an explicit UTC calendar date.
        var rows = await db.Database.SqlQuery<DailySalesPoint>($"""
            WITH daily AS (
                SELECT DISTINCT ON ("Metric", ("PeriodUtc" AT TIME ZONE 'UTC')::date)
                    ("PeriodUtc" AT TIME ZONE 'UTC')::date AS day,
                    "Metric", "Value", "ObservedAtUtc"
                FROM "MarketHistoryPoints"
                WHERE "ItemId" = {itemId} AND "Source" = 'marketplace-sales'
                    AND "Metric" IN ('PrecioMedioVenta', 'VolumenVentas')
                    AND "PeriodUtc" >= {start} AND "PeriodUtc" < {end}
                    AND ("PeriodUtc" AT TIME ZONE 'UTC')::date > {cursor}
                ORDER BY "Metric", ("PeriodUtc" AT TIME ZONE 'UTC')::date,
                    "PeriodUtc" DESC, "ObservedAtUtc" DESC
            )
            SELECT day AS "DateUtc",
                MAX("Value") FILTER (WHERE "Metric" = 'PrecioMedioVenta') AS "AveragePriceRobux",
                MAX("Value") FILTER (WHERE "Metric" = 'VolumenVentas') AS "SalesVolume",
                MAX("ObservedAtUtc") AS "UpdatedAtUtc"
            FROM daily GROUP BY day ORDER BY day LIMIT {limit + 1}
            """).ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);
        return new(from, to, rows, more ? rows[^1].DateUtc : null);
    }

    public async Task<RapHistoryPage> GetRapAsync(Guid itemId, DateOnly from, DateOnly to, DateTimeOffset? after, int limit, CancellationToken cancellationToken)
    {
        Validate(from, to, limit);
        var start = MarketSeriesRange.Utc(from);
        var end = MarketSeriesRange.Utc(to);
        var cursor = after?.UtcDateTime ?? DateTime.MinValue;
        var rows = await db.Set<MarketRapSnapshot>().AsNoTracking()
            .Where(x => x.ItemId == itemId && x.ObservedAtUtc >= start && x.ObservedAtUtc < end && x.ObservedAtUtc > cursor)
            .OrderBy(x => x.ObservedAtUtc)
            .Select(x => new RapHistoryPoint(x.ObservedAtUtc, x.Value))
            .Take(limit + 1).ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);
        return new(from, to, rows, more ? new DateTimeOffset(rows[^1].ObservedAtUtc) : null);
    }

    private static void Validate(DateOnly from, DateOnly to, int limit)
    {
        if (!MarketSeriesRange.IsValid(from, to, limit))
            throw new ArgumentException("Use 1–500 points and a positive range of at most 366 days.");
    }
}
