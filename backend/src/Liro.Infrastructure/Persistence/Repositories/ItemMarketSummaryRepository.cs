using Liro.Application.Catalog.Repositories;
using Microsoft.EntityFrameworkCore;
using Liro.Infrastructure.Market;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemMarketSummaryRepository(LiroDbContext db) : IItemMarketSummaryRepository
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

    public Task<ItemMarketSummary?> GetAsync(Guid itemId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<ItemMarketSummary>($"""
            SELECT i."Id" AS "ItemId", i."RobloxAssetId" AS "RobloxItemId", i."ItemType", i."Name", i."MarketplaceUrl", i."ThumbnailUrl",
                s."Data_LowestResalePriceRobux" AS "LowestResalePriceRobux", s."Data_HasResellers" AS "HasResellers",
                s."ObservedAtUtc" AS "PriceObservedAtUtc", s."Source" AS "PriceSource",
                w."RecentAveragePrice", w."RapObservedAtUtc", w."Activity", w."DailySales"
            FROM "Items" i LEFT JOIN "ItemMarketState" s ON s."ItemId"=i."Id"
            LEFT JOIN "MarketWatches" w ON w."ItemId"=i."Id"
            WHERE i."Id"={itemId}
            """).SingleOrDefaultAsync(cancellationToken);
}
