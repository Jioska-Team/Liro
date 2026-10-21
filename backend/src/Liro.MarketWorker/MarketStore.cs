using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liro.MarketWorker;

public sealed class MarketClaim
{
    public Guid ItemId
    {
        get; set;
    }
    public Guid CollectibleItemId
    {
        get; set;
    }
    public Guid LeaseId
    {
        get; set;
    }
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

    public Task<List<MarketPopulation>> PopulationAsync(CancellationToken ct) => db.Database.SqlQueryRaw<MarketPopulation>("""
        SELECT w."Activity", COUNT(*)::int AS "Count" FROM "MarketWatches" w JOIN "Items" i ON i."Id"=w."ItemId"
        WHERE i."CreatorId"=1 AND i."CreatorType"='User' AND i."MarketStatus" IN ('Limited','LimitedUnique')
          AND i."CollectibleItemId" IS NOT NULL GROUP BY w."Activity"
        """).ToListAsync(ct);

    public Task<List<MarketActivityEvidence>> ActivityEvidenceAsync(DateTime asOf, Guid? itemId, CancellationToken ct)
        => ActivityEvidenceQuery(asOf, itemId).ToListAsync(ct);

    private IQueryable<MarketActivityEvidence> ActivityEvidenceQuery(DateTime asOf, Guid? itemId)
        => db.Database.SqlQuery<MarketActivityEvidence>($"""
            SELECT w."ItemId", w."LastSuccessAtUtc", r."CompletedDays", r."WeeklySales", r."HasRecent",
                EXISTS(SELECT 1 FROM "MarketHistoryPoints" h WHERE h."ItemId"=w."ItemId"
                    AND h."Source"='marketplace-sales' AND h."Metric"='VolumenVentas') AS "HasHistory"
            FROM "MarketWatches" w JOIN "Items" i ON i."Id"=w."ItemId"
            CROSS JOIN LATERAL (
                SELECT COUNT(*) FILTER(WHERE (h."PeriodUtc" AT TIME ZONE 'UTC')::time='00:00:00')::int AS "CompletedDays",
                    COALESCE(SUM(h."Value") FILTER(WHERE (h."PeriodUtc" AT TIME ZONE 'UTC')::time='00:00:00'),0) AS "WeeklySales",
                    COUNT(*)>0 AS "HasRecent"
                FROM "MarketHistoryPoints" h WHERE h."ItemId"=w."ItemId" AND h."Source"='marketplace-sales'
                    AND h."Metric"='VolumenVentas' AND h."PeriodUtc">={asOf.Date.AddDays(-7)} AND h."PeriodUtc"<{asOf.Date}
            ) r
            WHERE ({itemId}::uuid IS NULL OR w."ItemId"={itemId}) AND i."CreatorId"=1 AND i."CreatorType"='User'
                AND i."MarketStatus" IN ('Limited','LimitedUnique') AND i."CollectibleItemId" IS NOT NULL
            """);

    public async Task<int> ReclassifyAsync(DateTime asOf, CancellationToken ct, SemaphoreSlim? writes = null)
    {
        var changed = 0;
        Guid? after = null;
        while (true)
        {
            var query = ActivityEvidenceQuery(asOf, null);
            if (after is { } cursor)
                query = query.Where(x => x.ItemId.CompareTo(cursor) > 0);
            var batch = await query.OrderBy(x => x.ItemId).Take(120).ToListAsync(ct);
            if (batch.Count == 0)
                return changed;
            var ids = batch.Select(x => x.ItemId).ToArray();
            var last = batch.Select(x => x.LastSuccessAtUtc).ToArray();
            var activities = batch.Select(x => x.Activity).ToArray();
            var daily = batch.Select(x => x.ExactDailySales).ToArray();
            // Read and pace outside the gate; only this bounded update competes with saves/claims.
            if (writes is not null)
                await writes.WaitAsync(ct);
            try
            {
                changed += await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MarketWatches" w SET "Activity"=e.activity, "DailySales"=round(e.daily,6)
                FROM unnest({ids}::uuid[],{last}::timestamptz[],{activities}::text[],{daily}::numeric[]) e(id,last,activity,daily)
                WHERE w."ItemId"=e.id AND w."LastSuccessAtUtc" IS NOT DISTINCT FROM e.last
                    AND (w."LeaseUntilUtc" IS NULL OR w."LeaseUntilUtc"<=NOW())
                    AND (w."Activity" IS DISTINCT FROM e.activity OR w."DailySales" IS DISTINCT FROM round(e.daily,6))
                """, ct);
            }
            finally { writes?.Release(); }
            after = batch[^1].ItemId;
            await Task.Delay(25, ct);
            if (batch.Count < 120)
                return changed;
        }
    }

    public async Task<MarketClaim?> ClaimAsync(CancellationToken ct, bool preferBackground = false, MarketOptions? schedule = null)
    {
        var lease = Guid.NewGuid();
        var plan = schedule ?? new MarketOptions();
        // Keep the reserved background turn first, then prefer due very-active items over
        // the older high-activity backlog. Eligibility, cooldowns and leases still apply.
        var rows = await db.Database.SqlQuery<MarketClaim>($"""
            WITH candidate AS (
                SELECT w."ItemId" FROM "MarketWatches" w JOIN "Items" i ON i."Id" = w."ItemId"
                CROSS JOIN LATERAL (SELECT CASE
                    WHEN {schedule != null} AND w."LastSuccessAtUtc" IS NOT NULL
                        AND (w."LastErrorCode" IS NULL OR w."LastErrorCode" LIKE 'Cupo%')
                    THEN w."LastSuccessAtUtc" + INTERVAL '1 minute' * CASE w."Activity"
                        WHEN 'MuyAlta' THEN {plan.VeryActiveMinutes} WHEN 'Alta' THEN {plan.ActiveMinutes}
                        WHEN 'Media' THEN {plan.NormalMinutes} WHEN 'Inactiva' THEN {plan.InactiveMinutes}
                        WHEN 'SinVentasRecientes' THEN {plan.InactiveMinutes}
                        WHEN 'InciertaReciente' THEN {plan.UncertainMinutes}
                        WHEN 'SinHistorial' THEN {plan.MissingHistoryMinutes} WHEN 'SinDatos' THEN {plan.MissingHistoryMinutes}
                        ELSE {plan.LowMinutes} END
                    ELSE w."NextCheckAtUtc" END AS due) q
                WHERE q.due <= NOW() AND (w."LeaseUntilUtc" IS NULL OR w."LeaseUntilUtc" <= NOW())
                  AND i."CreatorId" = 1 AND i."CreatorType" = 'User'
                  AND i."MarketStatus" IN ('Limited', 'LimitedUnique') AND i."CollectibleItemId" IS NOT NULL
                ORDER BY CASE WHEN (w."Activity" IN ('MuyAlta','Alta')) = {!preferBackground} THEN 0 ELSE 1 END,
                    CASE WHEN w."Activity"='MuyAlta' THEN 0 ELSE 1 END,
                    q.due, w."ItemId" FOR UPDATE OF w SKIP LOCKED LIMIT 1
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
        if (watch is null)
            return false;
        if (watch.LastSuccessAtUtc > observation.ObservedAtUtc)
            return false;
        if (observation.Points.Count > 0)
        {
            var points = observation.Points.OrderBy(p => p.Metric, StringComparer.Ordinal).ThenBy(p => p.PeriodUtc).ToArray();
            var metrics = points.Select(p => p.Metric).ToArray();
            var dates = points.Select(p => p.PeriodUtc).ToArray();
            var values = points.Select(p => p.Value).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MarketHistoryPoints" ("ItemId", "Source", "Metric", "PeriodUtc", "Value", "ObservedAtUtc")
                SELECT {claim.ItemId}, 'marketplace-sales', p.metric, p.period, p.value, {observation.ObservedAtUtc}
                FROM unnest({metrics}::text[], {dates}::timestamptz[], {values}::numeric[]) AS p(metric, period, value)
                ON CONFLICT ("ItemId", "Source", "Metric", "PeriodUtc") DO UPDATE
                SET "Value"=EXCLUDED."Value", "ObservedAtUtc"=EXCLUDED."ObservedAtUtc"
                WHERE "MarketHistoryPoints"."ObservedAtUtc" <= EXCLUDED."ObservedAtUtc"
                  AND "MarketHistoryPoints"."Value" IS DISTINCT FROM EXCLUDED."Value"
                """, ct);
        }
        if (observation.Rap is { } rap)
        {
            if (watch.RecentAveragePrice != rap)
                db.Set<MarketRapSnapshot>().Add(new()
                {
                    ItemId = claim.ItemId,
                    Value = rap,
                    ObservedAtUtc = observation.ObservedAtUtc
                });
            watch.RecentAveragePrice = rap;
            watch.RapObservedAtUtc = observation.ObservedAtUtc;
        }
        var evidence = (await ActivityEvidenceAsync(observation.ObservedAtUtc, claim.ItemId, ct)).Single();
        watch.DailySales = evidence.ExactDailySales;
        watch.Activity = evidence.Activity;
        watch.NextCheckAtUtc = observation.ObservedAtUtc + options.IntervalFor(evidence.Activity);
        watch.LastSuccessAtUtc = observation.ObservedAtUtc;
        watch.ConsecutiveFailures = 0;
        watch.LastErrorCode = null;
        watch.LeaseId = null;
        watch.LeaseUntilUtc = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public Task<int> ReleaseAsync(MarketClaim claim, string code, TimeSpan minimumDelay, bool failure, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MarketWatches" SET "LeaseId"=NULL,
              "LeaseUntilUtc"=CASE WHEN {code.StartsWith("Cupo", StringComparison.Ordinal)} THEN NOW() + {minimumDelay} ELSE NULL END,
              "LastErrorCode"={code},
              "ConsecutiveFailures"="ConsecutiveFailures" + {(failure ? 1 : 0)},
              "NextCheckAtUtc"=CASE WHEN {code.StartsWith("Cupo", StringComparison.Ordinal)} THEN "NextCheckAtUtc"
              ELSE NOW() + GREATEST({minimumDelay},
                CASE WHEN {failure} THEN INTERVAL '1 minute' * LEAST(180, power(2, LEAST("ConsecutiveFailures", 8)))
                ELSE INTERVAL '0 seconds' END) END
            WHERE "ItemId"={claim.ItemId} AND "LeaseId"={claim.LeaseId}
            """, ct);
}
