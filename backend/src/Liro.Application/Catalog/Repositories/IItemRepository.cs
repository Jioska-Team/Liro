using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Repositories;

public interface IItemRepository
{
    Task<bool> IsOfficialBundleComponentAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    async Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await GetByIdAsync(id, cancellationToken) is not null;
    Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Item?> GetByAssetIdAsync(long robloxAssetId, CancellationToken cancellationToken = default);
    Task<bool> ExistsByAssetIdAsync(long robloxAssetId, CancellationToken cancellationToken = default);
    Task DeferRefreshAsync(long assetId, DateTime retryAtUtc, CancellationToken cancellationToken = default);
    Task DeleteByAssetIdAsync(long assetId, CancellationToken cancellationToken = default);
    Task AddAsync(Item item, CancellationToken cancellationToken = default);
    Task UpdateAsync(Item item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetStaleAssetIdsAsync(DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default);
    async Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed repository required for bundles.");
        }
        var item = await GetByAssetIdAsync(key.Id, cancellationToken);
        return item;
    }
    async Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime dueAtUtc, int limit, CancellationToken cancellationToken = default)
    {
        var ids = await GetStaleAssetIdsAsync(dueAtUtc, limit, cancellationToken);
        var items = ids.Select(id => new CatalogItemKey(id)).ToArray();
        return items;
    }
    async Task DeferRefreshAsync(CatalogItemKey key, DateTime retryAtUtc, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed repository required for bundles.");
        }
        await DeferRefreshAsync(key.Id, retryAtUtc, cancellationToken);
    }
    async Task DeleteAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed repository required for bundles.");
        }
        await DeleteByAssetIdAsync(key.Id, cancellationToken);
    }
}

