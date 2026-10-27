using Liro.Application.Catalog.Services;
using Microsoft.Extensions.Options;
using Liro.Application.Catalog.Repositories;
using Microsoft.EntityFrameworkCore;
using Liro.Infrastructure.Market;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemMarketSummaryRepository(LiroDbContext db, IOptions<MarketDashboardOptions>? options = null) : IItemMarketSummaryRepository
{
    public async Task<IReadOnlyDictionary<Guid, decimal?>> GetRapAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
            return new Dictionary<Guid, decimal?>();
        return await db.Set<MarketWatch>().AsNoTracking()
            .Where(watch => itemIds.Contains(watch.ItemId))
            .Select(watch => new { watch.ItemId, watch.RecentAveragePrice })
            .ToDictionaryAsync(watch => watch.ItemId, watch => watch.RecentAveragePrice, cancellationToken);
    }

    public Task<ItemMarketSummary?> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var settings = options?.Value ?? new MarketDashboardOptions();
        var freshAfter = now.AddMinutes(-settings.MaxRapAgeMinutes);
        return db.Database.SqlQuery<ItemMarketSummary>($"""
            SELECT i."Id" AS "ItemId", i."RobloxAssetId" AS "RobloxItemId", i."ItemType", i."Name", i."MarketplaceUrl", i."ThumbnailUrl", i."AssetTypeId",
                s."Data_TotalQuantity" AS "TotalQuantity", s."Data_UnitsAvailableForConsumption" AS "UnitsAvailableForConsumption",
                s."Data_FavoriteCount" AS "FavoriteCount",
                s."Data_LowestResalePriceRobux" AS "LowestResalePriceRobux", s."Data_HasResellers" AS "HasResellers",
                s."ObservedAtUtc" AS "PriceObservedAtUtc", s."Source" AS "PriceSource",
                w."RecentAveragePrice", w."RapObservedAtUtc", w."Activity",
                CASE WHEN sales.days=7 AND w."LastSuccessAtUtc">={freshAfter} AND w."LastSuccessAtUtc"<={now}
                    AND i."MarketStatus" IN ('Limited','LimitedUnique') THEN sales.average END AS "DailySales",
                7 AS "DailySalesWindowDays", {now.Date.AddDays(-7)} AS "DailySalesPeriodStartUtc", {now.Date} AS "DailySalesPeriodEndUtc",
                CASE WHEN sales.days=7 AND w."LastSuccessAtUtc">={freshAfter} AND w."LastSuccessAtUtc"<={now}
                    AND i."MarketStatus" IN ('Limited','LimitedUnique') THEN sales.observed END AS "DailySalesObservedAtUtc",
                CASE WHEN w."RapObservedAtUtc" <= {now} THEN EXTRACT(EPOCH FROM ({now}-w."RapObservedAtUtc"))::double precision END AS "RapAgeSeconds",
                CASE WHEN w."RecentAveragePrice" IS NOT NULL THEN 'marketplace-sales' END AS "RapSource",
                CASE WHEN w."RapObservedAtUtc" >= {now.AddMinutes(-Math.Min(settings.MaxRapAgeMinutes, 15))} AND w."RapObservedAtUtc" <= {now} AND w."RapObservedAtUtc" > {now.AddHours(-1)} AND w."RecentAveragePrice" >= 0 AND b1."Value" > 0 THEN (w."RecentAveragePrice"-b1."Value")/b1."Value"*100 END AS "RapChange1hPercent",
                CASE WHEN w."RapObservedAtUtc" >= {freshAfter} AND w."RapObservedAtUtc" <= {now} AND w."RapObservedAtUtc" > {now.AddHours(-24)} AND w."RecentAveragePrice" >= 0 AND b24."Value" > 0 THEN (w."RecentAveragePrice"-b24."Value")/b24."Value"*100 END AS "RapChange24hPercent",
                CASE WHEN w."RapObservedAtUtc" >= {freshAfter} AND w."RapObservedAtUtc" <= {now} AND w."RapObservedAtUtc" > {now.AddHours(-168)} AND w."RecentAveragePrice" >= 0 AND b168."Value" > 0 THEN (w."RecentAveragePrice"-b168."Value")/b168."Value"*100 END AS "RapChange7dPercent",
                CASE WHEN w."RapObservedAtUtc" >= {freshAfter} AND w."RapObservedAtUtc" <= {now} AND w."RapObservedAtUtc" > {now.AddHours(-720)} AND w."RecentAveragePrice" >= 0 AND b720."Value" > 0 THEN (w."RecentAveragePrice"-b720."Value")/b720."Value"*100 END AS "RapChange30dPercent"
            FROM "Items" i LEFT JOIN "ItemMarketState" s ON s."ItemId"=i."Id"
            LEFT JOIN "MarketWatches" w ON w."ItemId"=i."Id"
            LEFT JOIN LATERAL (SELECT r."Value" FROM "MarketRapSnapshots" r WHERE r."ItemId"=i."Id" AND r."ObservedAtUtc" <= {now.AddHours(-1)} AND r."ObservedAtUtc" >= {now.AddHours(-1).AddMinutes(-Math.Min(settings.BaselineToleranceMinutes, 15))} ORDER BY r."ObservedAtUtc" DESC LIMIT 1) b1 ON true
            LEFT JOIN LATERAL (SELECT r."Value" FROM "MarketRapSnapshots" r WHERE r."ItemId"=i."Id" AND r."ObservedAtUtc" <= {now.AddHours(-24)} AND r."ObservedAtUtc" >= {now.AddHours(-24).AddMinutes(-Math.Min(settings.BaselineToleranceMinutes, 1440))} ORDER BY r."ObservedAtUtc" DESC LIMIT 1) b24 ON true
            LEFT JOIN LATERAL (SELECT r."Value" FROM "MarketRapSnapshots" r WHERE r."ItemId"=i."Id" AND r."ObservedAtUtc" <= {now.AddHours(-168)} AND r."ObservedAtUtc" >= {now.AddHours(-168).AddMinutes(-Math.Min(settings.BaselineToleranceMinutes, 10080))} ORDER BY r."ObservedAtUtc" DESC LIMIT 1) b168 ON true
            LEFT JOIN LATERAL (SELECT r."Value" FROM "MarketRapSnapshots" r WHERE r."ItemId"=i."Id" AND r."ObservedAtUtc" <= {now.AddHours(-720)} AND r."ObservedAtUtc" >= {now.AddHours(-720).AddMinutes(-Math.Min(settings.BaselineToleranceMinutes, 43200))} ORDER BY r."ObservedAtUtc" DESC LIMIT 1) b720 ON true
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int days, AVG(h."Value") average, MAX(h."ObservedAtUtc") observed
                FROM "MarketHistoryPoints" h
                WHERE h."ItemId"=i."Id" AND h."Source"='marketplace-sales' AND h."Metric"='VolumenVentas'
                    AND h."PeriodUtc">={now.Date.AddDays(-7)} AND h."PeriodUtc"<{now.Date}
                    AND (h."PeriodUtc" AT TIME ZONE 'UTC')::time='00:00:00'
                    AND h."ObservedAtUtc">=h."PeriodUtc"+INTERVAL '24 hours' AND h."ObservedAtUtc"<={now}
                    AND h."Value">=0 AND h."Value"=trunc(h."Value")
            ) sales ON true
            WHERE i."Id"={itemId}
            """).SingleOrDefaultAsync(cancellationToken);
    }
}
