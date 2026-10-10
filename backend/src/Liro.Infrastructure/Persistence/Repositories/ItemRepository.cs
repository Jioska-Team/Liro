using Liro.Application.Catalog.Repositories;
using Liro.Application.Common;
using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemRepository(LiroDbContext dbContext) : IItemRepository
{
    public async Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.Items
            .Include(x => x.MarketState)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return item;
    }

    public async Task<Item?> GetByAssetIdAsync(long assetId, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.Items
            .Include(x => x.MarketState)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RobloxAssetId == assetId, cancellationToken);

        return item;
    }

    public async Task<bool> ExistsByAssetIdAsync(long assetId, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Items
            .AnyAsync(x => x.RobloxAssetId == assetId, cancellationToken);

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

    public async Task<IReadOnlyList<long>> GetStaleAssetIdsAsync(DateTime dueAtUtc, int limit, CancellationToken cancellationToken = default)
    {
        var assetIds = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.NextRefreshAt <= dueAtUtc
                && !dbContext.Set<CatalogImportFailure>().Any(f => f.AssetId == x.RobloxAssetId))
            .OrderBy(x => x.NextRefreshAt)
            .ThenBy(x => x.Id)
            .Select(x => x.RobloxAssetId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return assetIds;
    }

    public async Task DeferRefreshAsync(long assetId, DateTime retryAtUtc, CancellationToken cancellationToken = default)
    {
        await dbContext.Items
            .Where(x => x.RobloxAssetId == assetId && x.NextRefreshAt < retryAtUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRefreshAt, retryAtUtc), cancellationToken);
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
