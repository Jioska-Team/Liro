using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Repositories;

public interface IItemRepository
{
    Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Item?> GetByAssetIdAsync(long robloxAssetId, CancellationToken cancellationToken = default);
    Task<bool> ExistsByAssetIdAsync(long robloxAssetId, CancellationToken cancellationToken = default);
    Task DeferRefreshAsync(long assetId, DateTime retryAtUtc, CancellationToken cancellationToken = default);
    Task AddAsync(Item item, CancellationToken cancellationToken = default);
    Task UpdateAsync(Item item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetStaleAssetIdsAsync(DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default);
}
