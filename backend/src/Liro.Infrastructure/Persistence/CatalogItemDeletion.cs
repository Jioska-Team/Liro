using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

internal static class CatalogItemDeletion
{
    public static async Task<int> DeleteAsync(LiroDbContext db, IQueryable<Item> candidates, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
            var items = await candidates.Select(item => new { item.Id, item.RobloxAssetId, item.ItemType }).ToListAsync(token);
            if (items.Count == 0)
            {
                await transaction.CommitAsync(token);
                return 0;
            }

            var itemIds = items.Select(item => item.Id).ToArray();

            await db.Set<ItemMarketSnapshot>().Where(snapshot => itemIds.Contains(snapshot.ItemId)).ExecuteDeleteAsync(token);
            await db.Set<ItemMarketState>().Where(state => itemIds.Contains(state.ItemId)).ExecuteDeleteAsync(token);
            foreach (var group in items.GroupBy(item => item.ItemType))
            {
                var itemType = group.Key;
                var ids = group.Select(item => item.RobloxAssetId).ToArray();
                await db.Set<CatalogImportFailure>().Where(failure => failure.ItemType == itemType && ids.Contains(failure.AssetId)).ExecuteDeleteAsync(token);
            }
            var deleted = await db.Items.Where(item => itemIds.Contains(item.Id)).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
            return deleted;
        }, cancellationToken);
        return result;
    }
}

