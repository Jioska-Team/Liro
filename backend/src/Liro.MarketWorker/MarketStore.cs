using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liro.MarketWorker;

public sealed class MarketClaim
{
    public Guid ItemId { get; set; }
    public Guid CollectibleItemId { get; set; }
    public Guid LeaseId { get; set; }
}

public sealed class MarketStore(LiroDbContext db)
{
    public Task<List<CatalogoGeneral>> ConnectionsAsync(CancellationToken ct) => db.Set<CatalogoGeneral>().AsNoTracking()
        .Where(x => x.Tipo == "CONEXION_MERCADO" && x.Activo).OrderBy(x => x.Clave).Take(33).ToListAsync(ct);

    public Task<int> SeedAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO "MarketWatches" ("ItemId", "NextCheckAtUtc", "ConsecutiveFailures", "Activity")
        SELECT "Id", NOW(), 0, 'SinDatos' FROM "Items"
        WHERE "CreatorId" = 1 AND "CreatorType" = 'User'
          AND "MarketStatus" IN ('Limited', 'LimitedUnique') AND "CollectibleItemId" IS NOT NULL
        ON CONFLICT ("ItemId") DO NOTHING
        """, ct);

    public async Task<MarketClaim?> ClaimAsync(CancellationToken ct)
    {
        var lease = Guid.NewGuid();
        var rows = await db.Database.SqlQuery<MarketClaim>($"""
            WITH candidate AS (
                SELECT w."ItemId" FROM "MarketWatches" w JOIN "Items" i ON i."Id" = w."ItemId"
                WHERE w."NextCheckAtUtc" <= NOW() AND (w."LeaseUntilUtc" IS NULL OR w."LeaseUntilUtc" <= NOW())
                  AND i."CreatorId" = 1 AND i."CreatorType" = 'User'
                  AND i."MarketStatus" IN ('Limited', 'LimitedUnique') AND i."CollectibleItemId" IS NOT NULL
                ORDER BY w."NextCheckAtUtc", w."ItemId" FOR UPDATE OF w SKIP LOCKED LIMIT 1
            ), claimed AS (
                UPDATE "MarketWatches" w SET "LeaseId" = {lease}, "LeaseUntilUtc" = NOW() + INTERVAL '5 minutes',
                    "LastAttemptAtUtc" = NOW()
                FROM candidate c WHERE w."ItemId" = c."ItemId" RETURNING w."ItemId", w."LeaseId"
            ) SELECT c."ItemId", c."LeaseId", i."CollectibleItemId" FROM claimed c JOIN "Items" i ON i."Id"=c."ItemId"
            """).ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task<bool> SaveAsync(MarketClaim claim, ResaleObservation observation, MarketOptions options, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Set<MarketWatch>().FromSqlInterpolated($"""
            SELECT * FROM "MarketWatches" WHERE "ItemId"={claim.ItemId} AND "LeaseId"={claim.LeaseId}
            AND "LeaseUntilUtc" > NOW() FOR UPDATE
            """).ToListAsync(ct);
        var watch = rows.SingleOrDefault();
        if (watch is null) return false;
        if (watch.LastSuccessAtUtc > observation.ObservedAtUtc) return false;
        foreach (var group in observation.Points.GroupBy(p => p.Metric))
        {
            var dates = group.Select(p => p.PeriodUtc).ToArray();
            var values = group.Select(p => p.Value).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MarketHistoryPoints" ("ItemId", "Source", "Metric", "PeriodUtc", "Value", "ObservedAtUtc")
                SELECT {claim.ItemId}, 'marketplace-sales', {group.Key}, p.period, p.value, {observation.ObservedAtUtc}
                FROM unnest({dates}::timestamptz[], {values}::numeric[]) AS p(period, value)
                ON CONFLICT ("ItemId", "Source", "Metric", "PeriodUtc") DO UPDATE
                SET "Value"=EXCLUDED."Value", "ObservedAtUtc"=EXCLUDED."ObservedAtUtc"
                WHERE "MarketHistoryPoints"."ObservedAtUtc" <= EXCLUDED."ObservedAtUtc"
                  AND "MarketHistoryPoints"."Value" IS DISTINCT FROM EXCLUDED."Value"
                """, ct);
        }
        if (observation.Rap is { } rap)
        {
            if (watch.RecentAveragePrice != rap)
                db.Set<MarketRapSnapshot>().Add(new() { ItemId = claim.ItemId, Value = rap, ObservedAtUtc = observation.ObservedAtUtc });
            watch.RecentAveragePrice = rap;
            watch.RapObservedAtUtc = observation.ObservedAtUtc;
        }
        watch.DailySales = observation.DailySales();
        var schedule = options.Schedule(watch.DailySales);
        watch.Activity = schedule.Activity;
        watch.NextCheckAtUtc = observation.ObservedAtUtc + schedule.Interval;
        watch.LastSuccessAtUtc = observation.ObservedAtUtc;
        watch.ConsecutiveFailures = 0;
        watch.LastErrorCode = null;
        watch.LeaseId = null; watch.LeaseUntilUtc = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public Task<int> ReleaseAsync(MarketClaim claim, string code, TimeSpan minimumDelay, bool failure, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MarketWatches" SET "LeaseId"=NULL, "LeaseUntilUtc"=NULL, "LastErrorCode"={code},
              "ConsecutiveFailures"="ConsecutiveFailures" + { (failure ? 1 : 0) },
              "NextCheckAtUtc"=NOW() + GREATEST({minimumDelay},
                CASE WHEN {failure} THEN INTERVAL '1 minute' * LEAST(180, power(2, LEAST("ConsecutiveFailures", 8)))
                ELSE INTERVAL '0 seconds' END)
            WHERE "ItemId"={claim.ItemId} AND "LeaseId"={claim.LeaseId}
            """, ct);
}
