using Liro.Domain.Catalog.Models;
using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;

namespace Liro.RegressionTests;

sealed class MemoryRepository(Item item) : IItemRepository
{
    public int Saves;
    public List<long> DeletedAssetIds { get; } = [];
    public Task DeleteAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        DeletedAssetIds.Add(key.Id);
        return Task.CompletedTask;
    }
    public List<long> SavedAssetIds { get; } = [];

    public Task DeferRefreshAsync(CatalogItemKey key, DateTime time, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Item?>(id == item.Id ? item : null);
    public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult<Item?>(key.Id == item.RobloxAssetId && key.Type == item.ItemType ? item : null);
    public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(true);
    public Task AddAsync(Item i, CancellationToken ct = default)
    {
        Saves++;
        SavedAssetIds.Add(i.RobloxAssetId);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Item i, CancellationToken ct = default)
    {
        Saves++;
        SavedAssetIds.Add(i.RobloxAssetId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime t, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
}
