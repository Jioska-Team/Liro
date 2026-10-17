using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogLimitedWatchlist(LiroDbContext db) : ICatalogLimitedWatchlist
{
    public async Task TrackAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime now, CancellationToken ct)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required.", nameof(now));
        foreach (var key in keys) key.Validate();
        foreach (var chunk in keys.Distinct().Chunk(1000))
        {
            var types = chunk.Select(x => x.Type.ToString()).ToArray();
            var ids = chunk.Select(x => x.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CatalogLimitedWatches" ("ItemType", "ItemId", "AddedAtUtc", "NextCheckAtUtc")
                SELECT t, id, {now}, {DateTime.UnixEpoch} FROM unnest({types}, {ids}) AS k(t,id)
                ON CONFLICT ("ItemType", "ItemId") DO NOTHING
                """, ct);
        }
    }

    public Task SeedKnownAsync(DateTime now, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CatalogLimitedWatches" ("ItemType", "ItemId", "AddedAtUtc", "NextCheckAtUtc")
        SELECT "ItemType", "RobloxAssetId", {now}, {DateTime.UnixEpoch} FROM "Items"
        WHERE "CreatorId" = 1 AND "CreatorType" = 'User'
          AND "MarketStatus" IN ('Limited', 'LimitedUnique')
        ON CONFLICT ("ItemType", "ItemId") DO NOTHING
        """, ct);

    public async Task<IReadOnlyList<CatalogLimitedLease>> ClaimAsync(DateTime now, int limit, TimeSpan lease, CancellationToken ct)
    {
        if (now.Kind != DateTimeKind.Utc || limit is < 1 or > 100 || lease <= TimeSpan.Zero)
            throw new ArgumentException("Invalid watchlist claim.");
        var until = now.Add(lease);
        // Atomic claim. An interrupted process releases work by lease expiry; no offset can skip IDs.
        var rows = await db.Set<CatalogLimitedWatch>().FromSqlInterpolated($"""
            UPDATE "CatalogLimitedWatches" w SET "NextCheckAtUtc" = {until}
            FROM (
                SELECT x."ItemType", x."ItemId" FROM "CatalogLimitedWatches" x
                WHERE x."NextCheckAtUtc" <= {now}
                  AND NOT EXISTS (SELECT 1 FROM "CatalogImportFailures" f
                    WHERE f."ItemType"=x."ItemType" AND f."AssetId"=x."ItemId"
                      AND (f."DeadLetteredAtUtc" IS NOT NULL OR f."NextAttemptAtUtc" > {now}))
                ORDER BY x."LastConfirmedAtUtc" NULLS FIRST, x."NextCheckAtUtc", x."ItemType", x."ItemId"
                LIMIT {limit} FOR UPDATE OF x SKIP LOCKED
            ) due
            WHERE w."ItemType"=due."ItemType" AND w."ItemId"=due."ItemId"
            RETURNING w.*
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(x => new CatalogLimitedLease(new(x.ItemId, x.ItemType), x.NextCheckAtUtc)).ToArray();
    }

    public async Task CompleteAsync(IReadOnlyList<CatalogLimitedLease> entries, DateTime now, DateTime nextCheck, bool succeeded, CancellationToken ct)
    {
        foreach (var group in entries.GroupBy(x => x.LeaseUntilUtc))
        {
            var types = group.Select(x => x.Key.Type.ToString()).ToArray();
            var ids = group.Select(x => x.Key.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "CatalogLimitedWatches" w
                SET "NextCheckAtUtc"={nextCheck},
                    "LastConfirmedAtUtc"=CASE WHEN {succeeded} THEN {now} ELSE w."LastConfirmedAtUtc" END
                FROM unnest({types}, {ids}) AS k(t,id)
                WHERE w."ItemType"=k.t AND w."ItemId"=k.id AND w."NextCheckAtUtc"={group.Key}
                """, ct);
        }
    }

    public async Task CompleteObservedAsync(IReadOnlyList<CatalogLimitedLease> entries, IReadOnlyDictionary<CatalogItemKey, DateTime> observations, TimeSpan interval, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        var types = entries.Select(x => x.Key.Type.ToString()).ToArray();
        var ids = entries.Select(x => x.Key.Id).ToArray();
        var leases = entries.Select(x => x.LeaseUntilUtc).ToArray();
        var confirmed = entries.Select(x => observations[x.Key]).ToArray();
        var next = confirmed.Select(x => x.Add(interval)).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CatalogLimitedWatches" w
            SET "LastConfirmedAtUtc" = k.confirmed, "NextCheckAtUtc" = k.next
            FROM unnest({types}, {ids}, {leases}, {confirmed}, {next}) AS k(t,id,lease,confirmed,next)
            WHERE w."ItemType"=k.t AND w."ItemId"=k.id AND w."NextCheckAtUtc"=k.lease
            """, ct);
    }

    public async Task<CatalogLimitedWatchHealth> GetHealthAsync(DateTime now, CancellationToken ct)
    {
        var all = db.Set<CatalogLimitedWatch>();
        return new(await all.CountAsync(ct), await all.CountAsync(x => x.NextCheckAtUtc <= now, ct),
            await all.CountAsync(x => db.Set<CatalogImportFailure>().Any(f => f.ItemType == x.ItemType && f.AssetId == x.ItemId), ct),
            await all.MinAsync(x => x.LastConfirmedAtUtc, ct));
    }
}
