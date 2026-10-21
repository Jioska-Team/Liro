using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Repositories;

public interface IItemRepository
{
    Task<bool> IsOfficialBundleComponentAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    async Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await GetByIdAsync(id, cancellationToken) is not null;
    Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default);
    Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default);
    Task DeferRefreshAsync(CatalogItemKey key, DateTime retryAtUtc, CancellationToken cancellationToken = default);
    Task DeleteAsync(CatalogItemKey key, CancellationToken cancellationToken = default);
    Task AddAsync(Item item, CancellationToken cancellationToken = default);
    Task UpdateAsync(Item item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime dueAtUtc, int limit, CancellationToken cancellationToken = default);
}
