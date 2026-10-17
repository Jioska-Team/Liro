using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogPriorityQueue(LiroDbContext db) : ICatalogPriorityQueue
{
    public async Task EnqueueAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime detectedAtUtc, CancellationToken cancellationToken)
    {
        if (detectedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required.", nameof(detectedAtUtc));
        foreach (var key in keys.Distinct())
        {
            key.Validate();
            // Atomic deduplication preserves the original detection time across polls and restarts.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CatalogPriorityRefreshes" ("ItemType", "ItemId", "DetectedAtUtc")
                SELECT {key.Type.ToString()}, {key.Id}, {detectedAtUtc}
                WHERE NOT EXISTS (SELECT 1 FROM "Items" WHERE "ItemType" = {key.Type.ToString()}
                    AND "RobloxAssetId" = {key.Id} AND "MarketStatus" IN ('Limited', 'LimitedUnique'))
                ON CONFLICT ("ItemType", "ItemId") DO NOTHING
                """, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CatalogPriorityEntry>> GetDueAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        return await db.Set<CatalogPriorityRefresh>().AsNoTracking()
            .Where(x => !db.Set<CatalogImportFailure>().Any(f => f.ItemType == x.ItemType && f.AssetId == x.ItemId
                && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.DetectedAtUtc).ThenBy(x => x.ItemType).ThenBy(x => x.ItemId)
            .Take(limit).Select(x => new CatalogPriorityEntry(new CatalogItemKey(x.ItemId, x.ItemType), x.DetectedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public Task CompleteAsync(CatalogPriorityEntry entry, CancellationToken cancellationToken) =>
        db.Set<CatalogPriorityRefresh>().Where(x => x.ItemType == entry.Key.Type && x.ItemId == entry.Key.Id
            && x.DetectedAtUtc == entry.DetectedAtUtc).ExecuteDeleteAsync(cancellationToken);

    public async Task<CatalogQueueHealth> GetHealthAsync(DateTime now, CancellationToken cancellationToken)
    {
        var pending = await db.Set<CatalogPriorityRefresh>().CountAsync(cancellationToken);
        var oldest = await db.Set<CatalogPriorityRefresh>().Select(x => (DateTime?)x.DetectedAtUtc).MinAsync(cancellationToken);
        var overdue = db.Items.Where(x => x.NextRefreshAt <= now);
        return new(pending, oldest, await overdue.CountAsync(cancellationToken),
            await overdue.Select(x => (DateTime?)x.NextRefreshAt).MinAsync(cancellationToken));
    }
}
