using System.Data;
using System.Text.Json;
using Liro.Application.Catalog.Services;
using Liro.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Liro.Infrastructure.Persistence.Repositories;

// Read requests fetch one stored snapshot. Aggregation runs only in the existing market worker.
public sealed class MarketDashboardRepository(LiroDbContext db, IOptions<MarketDashboardOptions> settings) : IMarketDashboardRepository
{
    public async Task<MarketDashboardSummary> GetLatestAsync(DateTime now, CancellationToken ct)
    {
        var snapshot = await db.Set<MarketDashboardSnapshot>().AsNoTracking().OrderByDescending(x => x.BucketUtc).FirstOrDefaultAsync(ct);
        if (snapshot is null) return MarketDashboardSummary.Empty;
        var value = JsonSerializer.Deserialize<MarketDashboardSummary>(snapshot.Payload)!;
        return value with { IsStale = now - snapshot.BucketUtc > TimeSpan.FromMinutes(settings.Value.MaxSnapshotAgeMinutes) };
    }

    public async Task<bool> CaptureAsync(DateTime now, CancellationToken ct)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required.", nameof(now));
        var options = settings.Value;
        if (!options.IsValid()) throw new InvalidOperationException("Invalid dashboard options.");
        var ticks = TimeSpan.FromMinutes(options.IntervalMinutes).Ticks;
        var asOf = new DateTime(now.Ticks / ticks * ticks, DateTimeKind.Utc);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // Transaction-scoped lock: multiple callers cannot publish the same bucket.
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(193472, 1) AS \"Value\"").SingleAsync(ct))
            return false;
        if (await db.Set<MarketDashboardSnapshot>().AnyAsync(x => x.BucketUtc == asOf, ct)) return false;
        var total = await db.Items.CountAsync(x => x.MarketStatus == Liro.Domain.Catalog.Enums.ItemMarketStatus.Limited
            || x.MarketStatus == Liro.Domain.Catalog.Enums.ItemMarketStatus.LimitedUnique, ct);
        var baseline = asOf.AddDays(-1);
        var currentMin = asOf.AddMinutes(-options.MaxRapAgeMinutes);
        var baselineMin = baseline.AddMinutes(-options.BaselineToleranceMinutes);
        var pulse = await db.Database.SqlQuery<PulseCounts>($"""
            SELECT COUNT(*) FILTER (WHERE c."Value" > b."Value")::int AS "Up",
                   COUNT(*) FILTER (WHERE c."Value" < b."Value")::int AS "Down",
                   COUNT(*) FILTER (WHERE c."Value" = b."Value")::int AS "Unchanged"
            FROM "Items" i
            JOIN LATERAL (
                SELECT "Value" FROM "MarketRapSnapshots"
                WHERE "ItemId"=i."Id" AND "ObservedAtUtc" <= {asOf} AND "ObservedAtUtc" >= {currentMin}
                ORDER BY "ObservedAtUtc" DESC LIMIT 1
            ) c ON c."Value" >= 0
            JOIN LATERAL (
                SELECT "Value" FROM "MarketRapSnapshots"
                WHERE "ItemId"=i."Id" AND "ObservedAtUtc" <= {baseline} AND "ObservedAtUtc" >= {baselineMin}
                ORDER BY "ObservedAtUtc" DESC LIMIT 1
            ) b ON b."Value" > 0
            WHERE i."MarketStatus" IN ('Limited','LimitedUnique')
            """).SingleAsync(ct);
        var start = asOf.Date.AddDays(-1);
        var end = asOf.Date;
        var previousStart = start.AddDays(-1);
        // Only complete UTC midnight buckets from the same source and matching observation.
        // No summing multiple snapshots, no merging different provider timestamps or missing days.
        var volume = await db.Database.SqlQuery<VolumeTotals>($"""
            WITH paired AS (
                SELECT p."ItemId", p."PeriodUtc", p."Value" * q."Value" AS amount
                FROM "MarketHistoryPoints" p
                JOIN "MarketHistoryPoints" q ON q."ItemId"=p."ItemId" AND q."Source"=p."Source"
                    AND q."PeriodUtc"=p."PeriodUtc" AND q."Metric"='VolumenVentas'
                JOIN "Items" i ON i."Id"=p."ItemId"
                WHERE p."Source"='marketplace-sales' AND p."Metric"='PrecioMedioVenta'
                    AND i."MarketStatus" IN ('Limited','LimitedUnique')
                    AND p."PeriodUtc" IN ({start}, {previousStart})
                    AND p."ObservedAtUtc"=q."ObservedAtUtc"
                    AND p."ObservedAtUtc" >= p."PeriodUtc" + INTERVAL '24 hours'
                    AND p."ObservedAtUtc" <= {asOf}
                    AND p."Value" >= 0 AND q."Value" >= 0 AND q."Value"=trunc(q."Value")
            ), current_day AS (SELECT * FROM paired WHERE "PeriodUtc"={start}),
               previous_day AS (SELECT * FROM paired WHERE "PeriodUtc"={previousStart}),
               compared AS (SELECT c.amount current_amount, p.amount previous_amount
                 FROM current_day c JOIN previous_day p ON c."ItemId"=p."ItemId")
            SELECT (SELECT COUNT(*)::int FROM current_day) AS "Included",
                   (SELECT SUM(amount) FROM current_day) AS "Total",
                   (SELECT COUNT(*)::int FROM compared) AS "Compared",
                   (SELECT SUM(current_amount) FROM compared) AS "CurrentCompared",
                   (SELECT SUM(previous_amount) FROM compared) AS "PreviousCompared"
            """).SingleAsync(ct);
        var week = asOf.AddDays(-7);
        var weekMin = week.AddMinutes(-options.MaxSnapshotAgeMinutes);
        var previousCount = await db.Set<MarketDashboardSnapshot>().AsNoTracking()
            .Where(x => x.BucketUtc <= week && x.BucketUtc >= weekMin)
            .OrderByDescending(x => x.BucketUtc).Select(x => (int?)x.LimitedsCount).FirstOrDefaultAsync(ct);
        var summary = new MarketDashboardSummary(asOf, false, new(), new()
        {
            Value = volume.Total, CoveragePercent = MarketDashboardCalculator.Coverage(volume.Included, total),
            IncludedItems = volume.Included, ComparisonItems = volume.Compared,
            ChangePercent = volume.CurrentCompared is { } current && volume.PreviousCompared is { } previous
                ? MarketDashboardCalculator.Change(current, previous) : null,
            PeriodStartUtc = start, PeriodEndUtc = end,
            Status = volume.Included == 0 ? "Unavailable" : volume.Included == total ? "Available" : "Partial"
        }, new(total, previousCount is { } old ? total - old : null),
            MarketDashboardCalculator.Pulse(pulse.Up, pulse.Down, pulse.Unchanged, total, options));
        db.Set<MarketDashboardSnapshot>().Add(new() { BucketUtc = asOf, LimitedsCount = total, Payload = JsonSerializer.Serialize(summary) });
        await db.SaveChangesAsync(ct);
        // Hourly source observations remain in existing tables; compact global snapshots retain one year.
        await db.Set<MarketDashboardSnapshot>().Where(x => x.BucketUtc < asOf.AddDays(-366)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
    private sealed record PulseCounts(int Up, int Down, int Unchanged);
    private sealed record VolumeTotals(int Included, decimal? Total, int Compared, decimal? CurrentCompared, decimal? PreviousCompared);
}
