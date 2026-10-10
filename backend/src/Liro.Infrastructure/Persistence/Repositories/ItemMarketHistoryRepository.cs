using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class ItemMarketHistoryRepository(LiroDbContext db) : IItemMarketHistoryRepository
{
    public async Task<IReadOnlyList<ItemMarketSnapshot>> GetBeforeAsync(Guid itemId, DateTime beforeUtc, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200 || beforeUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("History requires a UTC cursor and a limit between 1 and 200.");
        }

        var snapshots = await db.Set<ItemMarketSnapshot>()
            .AsNoTracking()
            .Where(x => x.ItemId == itemId && x.ObservedAtUtc < beforeUtc)
            .OrderByDescending(x => x.ObservedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return snapshots;
    }
}
