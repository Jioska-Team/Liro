using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Repositories;

public interface IItemMarketHistoryRepository
{
    Task<IReadOnlyList<ItemMarketSnapshot>> GetBeforeAsync(Guid itemId, DateTime beforeUtc, int limit, CancellationToken cancellationToken);
}
