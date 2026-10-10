using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;

namespace Liro.RegressionTests;

sealed class MemoryRepository(Item item) : IItemRepository
{
    public int Saves;
    public List<long> SavedAssetIds { get; } = [];

    public Task DeferRefreshAsync(long id, DateTime time, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Item?>(id == item.Id ? item : null);
    public Task<Item?> GetByAssetIdAsync(long id, CancellationToken ct = default) => Task.FromResult<Item?>(id == item.RobloxAssetId ? item : null);
    public Task<bool> ExistsByAssetIdAsync(long id, CancellationToken ct = default) => Task.FromResult(true);
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

    public Task<IReadOnlyList<long>> GetStaleAssetIdsAsync(DateTime t, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<long>>([]);
}
