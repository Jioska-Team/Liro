using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Common;
using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemRepository(LiroDbContext dbContext) : IItemRepository
{
    public Task<bool> IsOfficialBundleComponentAsync(CatalogItemKey key, CancellationToken cancellationToken = default) =>
        key.Type != CatalogItemType.Asset ? Task.FromResult(false) : dbContext.Items.AnyAsync(b =>
            b.ItemType == CatalogItemType.Bundle && b.CreatorId == 1 && b.CreatorType == "User"
            && b.BundleComponentAssetIds.Contains(key.Id), cancellationToken);
    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.Items.AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.Items
            .Include(x => x.MarketState)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return item;
    }

    public async Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.Items
            .Include(x => x.MarketState)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RobloxAssetId == key.Id && x.ItemType == key.Type, cancellationToken);

        return item;
    }

    public async Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Items
            .AnyAsync(x => x.RobloxAssetId == key.Id && x.ItemType == key.Type, cancellationToken);

        return exists;
    }

    public async Task AddAsync(Item item, CancellationToken cancellationToken = default)
    {
        dbContext.Items.Add(item);
        await SaveAsync(cancellationToken);
    }

    public async Task UpdateAsync(Item item, CancellationToken cancellationToken = default)
    {
        dbContext.Items.Update(item);
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime dueAtUtc, int limit, CancellationToken cancellationToken = default)
    {
        var assetIds = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.NextRefreshAt <= dueAtUtc
                && !dbContext.Set<CatalogImportFailure>().Any(f => f.AssetId == x.RobloxAssetId && f.ItemType == x.ItemType))
            .OrderBy(x => x.NextRefreshAt)
            .ThenBy(x => x.Id)
            .Select(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return assetIds;
    }

    public async Task DeferRefreshAsync(CatalogItemKey key, DateTime retryAtUtc, CancellationToken cancellationToken = default)
    {
        await dbContext.Items
            .Where(x => x.RobloxAssetId == key.Id && x.ItemType == key.Type && x.NextRefreshAt < retryAtUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRefreshAt, retryAtUtc), cancellationToken);
    }

    public async Task DeleteAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        await CatalogItemDeletion.DeleteAsync(dbContext, dbContext.Items.Where(item => item.RobloxAssetId == key.Id && item.ItemType == key.Type), cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            dbContext.ChangeTracker.Clear();
            throw new ConflictException("The item was updated concurrently. Read it again before retrying.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.ChangeTracker.Clear();
            throw new ConflictException("An item with this asset or collectible ID already exists.", exception);
        }
    }
}

