using System.Text.Json;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Liro.RegressionTests;

internal static class MarketDataIntegrityTests
{
    // Well before real time: observation/window dates are deterministic within each test;
    // database leases still use PostgreSQL's clock, as in production.
    private static readonly DateTime Day = DateTime.UtcNow.Date.AddDays(-2);

    public static void Register(RegressionSuite suite)
    {
        foreach (var (minutes, value, expectedCount) in new[] { (59, 123m, 1), (60, 123m, 2), (61, 123m, 2), (15, 124m, 2) })
            suite.AsyncTest("MarketIntegrity_RapHeartbeat_" + minutes + "_" + value, async () =>
            {
                await using var db = await Open();
                var claim = await Seed(db, Day.AddHours(9));
                var observation = new ResaleObservation(value, [], Day.AddHours(9).AddMinutes(minutes));
                Check.That(await new MarketStore(db).SaveAsync(claim, observation, new(), default), "RAP observation rejected");
                Check.That(await db.Set<MarketRapSnapshot>().CountAsync() == expectedCount,
                    "Unchanged RAP must retain hourly evidence, without a snapshot on every poll");
                Check.That((await Watch(db)).RapObservedAtUtc == observation.ObservedAtUtc, "Watch freshness did not advance");
            });
        suite.AsyncTest("MarketIntegrity_RapHeartbeatRepairsMissingHistory", async () =>
        {
            await using var db = await Open();
            var claim = await Seed(db, Day.AddHours(9));
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "MarketRapSnapshots" """);
            var observation = new ResaleObservation(123, [], Day.AddHours(9).AddMinutes(1));
            Check.That(await new MarketStore(db).SaveAsync(claim, observation, new(), default), "Observation rejected");
            var snapshot = await db.Set<MarketRapSnapshot>().SingleAsync();
            Check.That(snapshot.ObservedAtUtc == observation.ObservedAtUtc && snapshot.Value == 123,
                "Missing RAP history must begin at the real new observation");
        });
        suite.AsyncTest("MarketIntegrity_OldReclassificationPreservesNewerWindow", async () =>
        {
            await using var db = await Open();
            var claim = await Seed(db, Day.AddMinutes(1), leased: false);
            // The new window is seven explicit zeros. The old window also includes
            // a high-sales day that has just expired at midnight.
            await Sales(db, claim.ItemId, 7, 0);
            await Point(db, claim.ItemId, "VolumenVentas", Day.AddDays(-8), 700);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "MarketWatches" SET "Activity"='Inactiva', "DailySales"=0""");
            var before = await Snapshot(db);
            var changed = await new MarketStore(db).ReclassifyAsync(Day.AddMinutes(-1), default);
            Check.That(changed == 0 && await Snapshot(db) == before,
                "An earlier classification window replaced data saved after its cutoff");
        });

        foreach (var lastSuccess in new DateTime?[] { null, Day, Day.AddTicks(-10) })
            suite.AsyncTest("MarketIntegrity_ReclassificationAcceptsCutoff_" +
                (lastSuccess is null ? "NoObservation" : lastSuccess == Day ? "Equal" : "Earlier"), async () =>
            {
                await using var db = await Open();
                var claim = await Seed(db, lastSuccess, leased: false);
                await Sales(db, claim.ItemId, 7, 0);
                var due = (await Watch(db)).NextCheckAtUtc;
                var store = new MarketStore(db);
                Check.That(await store.ReclassifyAsync(Day, default) == 1, "Eligible classification was skipped");
                var watch = await Watch(db);
                Check.That(watch.Activity == "Inactiva" && watch.DailySales == 0 && watch.NextCheckAtUtc == due,
                    "Valid classification or scheduling changed");
                Check.That(await store.ReclassifyAsync(Day, default) == 0, "Unchanged classification rewrites data");
            });

        suite.AsyncTest("MarketIntegrity_OnlySevenCompletedUtcDaysProveZeroSales", async () =>
        {
            await using var db = await Open();
            var claim = await Seed(db, Day.AddHours(-1), leased: false);
            // A non-UTC database session must not shift daily UTC boundaries.
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'America/Guayaquil'");
            await Sales(db, claim.ItemId, 6, 0);
            await Point(db, claim.ItemId, "VolumenVentas", Day.AddDays(-1).AddHours(12), 900);
            await Point(db, claim.ItemId, "VolumenVentas", Day, 900);
            await Point(db, claim.ItemId, "VolumenVentas", Day.AddDays(-8), 900);
            await Point(db, claim.ItemId, "PrecioMedioVenta", Day.AddDays(-7), 900);
            await Point(db, claim.ItemId, "VolumenVentas", Day.AddDays(-7), 900, "other-source");
            var store = new MarketStore(db);
            var partial = (await store.ActivityEvidenceAsync(Day, claim.ItemId, default)).Single();
            Check.That(partial.CompletedDays == 6 && partial.WeeklySales == 0 && partial.ExactDailySales is null
                && partial.Activity == "InciertaReciente", "Missing days or unrelated points became completed zero-sale days");
            await Point(db, claim.ItemId, "VolumenVentas", Day.AddDays(-7), 0);
            var complete = (await store.ActivityEvidenceAsync(Day, claim.ItemId, default)).Single();
            Check.That(complete.CompletedDays == 7 && complete.WeeklySales == 0 && complete.ExactDailySales == 0
                && complete.Activity == "Inactiva", "Seven explicit daily zeros must remain real zero sales");
        });

        suite.AsyncTest("MarketIntegrity_PartialObservationPreservesRapAndStoredSales", async () =>
        {
            await using var db = await Open();
            var last = Day.AddHours(10);
            var claim = await Seed(db, last);
            await Sales(db, claim.ItemId, 7, 10);
            var beforeRap = (await Watch(db)).RapObservedAtUtc;
            var observation = new ResaleObservation(null, [new("PrecioMedioVenta", Day.AddDays(-1), 50)], Day.AddHours(11));
            Check.That(await new MarketStore(db).SaveAsync(claim, observation, new(), default), "Partial response was not saved");
            var watch = await Watch(db);
            Check.That(watch.RecentAveragePrice == 123 && watch.RapObservedAtUtc == beforeRap
                && watch.Activity == "Alta" && watch.DailySales == 10 && watch.LastSuccessAtUtc == observation.ObservedAtUtc
                && watch.NextCheckAtUtc == observation.ObservedAtUtc.AddMinutes(new MarketOptions().ActiveMinutes),
                "Partial response erased RAP, changed the sales basis, or changed normal scheduling");
            Check.That(await db.Set<MarketHistoryPoint>().CountAsync() == 8
                && await db.Set<MarketHistoryPoint>().CountAsync(p => p.Metric == "VolumenVentas" && p.Value == 10) == 7
                && await db.Set<MarketRapSnapshot>().CountAsync() == 1, "Partial response lost stored sales or fabricated RAP history");
        });

        foreach (var (code, failure) in new[] { ("ErrorConsultaOGuardado", true), ("HTTP429", false), ("CupoMercado", false) })
            suite.AsyncTest("MarketIntegrity_ReleasePreservesMarketData_" + code, async () =>
            {
                await using var db = await Open();
                var claim = await Seed(db, Day.AddHours(10));
                await Sales(db, claim.ItemId, 7, 10);
                var before = await Snapshot(db, marketOnly: true);
                var due = (await Watch(db)).NextCheckAtUtc;
                Check.That(await new MarketStore(db).ReleaseAsync(claim, code, TimeSpan.FromMinutes(12), failure, default) == 1,
                    "Active reservation was not released");
                var watch = await Watch(db);
                Check.That(await Snapshot(db, marketOnly: true) == before, "Failure or cooldown erased market data or marked it fresh");
                Check.That(watch.ConsecutiveFailures == (failure ? 1 : 0) && watch.LastErrorCode == code && watch.LeaseId is null,
                    "Failure accounting or reservation release changed");
                Check.That(code == "CupoMercado" ? watch.NextCheckAtUtc == due && watch.LeaseUntilUtc > DateTime.UtcNow
                    : watch.NextCheckAtUtc > DateTime.UtcNow.AddMinutes(11), "Existing retry/priority policy changed");
            });

        foreach (var scenario in new[] { "ExpiredLease", "WrongLease", "OlderObservation" })
            suite.AsyncTest("MarketIntegrity_RejectedSaveWritesNothing_" + scenario, async () =>
            {
                await using var db = await Open();
                var last = Day.AddHours(10);
                var claim = await Seed(db, last);
                await Sales(db, claim.ItemId, 7, 10);
                if (scenario == "ExpiredLease")
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "MarketWatches" SET "LeaseUntilUtc"=NOW()-INTERVAL '1 minute'""");
                if (scenario == "WrongLease") claim.LeaseId = Guid.NewGuid();
                var before = await Snapshot(db);
                var observation = new ResaleObservation(999, [new("VolumenVentas", Day.AddDays(-1), 999)],
                    scenario == "OlderObservation" ? last.AddMinutes(-1) : last.AddMinutes(1));
                Check.That(!await new MarketStore(db).SaveAsync(claim, observation, new(), default) && await Snapshot(db) == before,
                    "A stale observation or invalid reservation wrote market data");
            });

        foreach (var cancelled in new[] { false, true })
            suite.AsyncTest("MarketIntegrity_TransactionRollsBackHistory_" + (cancelled ? "Cancellation" : "Failure"), async () =>
            {
                using var cancel = new CancellationTokenSource();
                var failure = new FailAfterHistoryWrite(cancelled ? cancel : null);
                await using var db = await Open(failure);
                var claim = await Seed(db, Day.AddHours(10));
                await Sales(db, claim.ItemId, 7, 10);
                var before = await Snapshot(db);
                var observation = new ResaleObservation(999, [new("PrecioMedioVenta", Day.AddDays(-1), 999)], Day.AddHours(11));
                var store = new MarketStore(db);
                if (cancelled)
                    await Check.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(claim, observation, new(), cancel.Token));
                else
                    await Check.ThrowsAsync<IntentionalSaveFailure>(() => store.SaveAsync(claim, observation, new(), default));
                Check.That(failure.RowsBeforeFailure == 8 && await Snapshot(db) == before,
                    "The history upsert was not exercised, or a failed transaction left partial data");
            });
    }

    private static async Task<LiroDbContext> Open(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LiroDbContext>()
            .UseNpgsql(IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new LiroDbContext(options.Options);
        try
        {
            await db.Database.OpenConnectionAsync();
            // Session-local tables shadow the application tables. No schema migration or external data is used.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE "Items" ("Id" uuid PRIMARY KEY, "CreatorId" bigint, "CreatorType" text, "MarketStatus" text, "CollectibleItemId" uuid);
                CREATE TEMP TABLE "MarketWatches" ("ItemId" uuid PRIMARY KEY, "NextCheckAtUtc" timestamptz NOT NULL,
                    "LastAttemptAtUtc" timestamptz, "LastSuccessAtUtc" timestamptz, "LeaseId" uuid, "LeaseUntilUtc" timestamptz,
                    "ConsecutiveFailures" integer NOT NULL, "LastErrorCode" varchar(60), "RecentAveragePrice" numeric(28,6),
                    "RapObservedAtUtc" timestamptz, "DailySales" numeric(28,6), "Activity" varchar(30) NOT NULL);
                CREATE TEMP TABLE "MarketHistoryPoints" ("ItemId" uuid, "Source" text, "Metric" text, "PeriodUtc" timestamptz,
                    "Value" numeric(28,6), "ObservedAtUtc" timestamptz, PRIMARY KEY ("ItemId","Source","Metric","PeriodUtc"));
                CREATE TEMP TABLE "MarketRapSnapshots" ("ItemId" uuid, "ObservedAtUtc" timestamptz, "Value" numeric(28,6),
                    PRIMARY KEY ("ItemId","ObservedAtUtc"));
                """);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async Task<MarketClaim> Seed(LiroDbContext db, DateTime? lastSuccess, bool leased = true)
    {
        var claim = new MarketClaim { ItemId = Guid.NewGuid(), CollectibleItemId = Guid.NewGuid(), LeaseId = Guid.NewGuid() };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Items" VALUES ({claim.ItemId},1,'User','Limited',{claim.CollectibleItemId});
            INSERT INTO "MarketWatches" VALUES ({claim.ItemId},{Day.AddHours(12)},{Day.AddHours(9)},{lastSuccess},
                {(leased ? (Guid?)claim.LeaseId : null)},CASE WHEN {leased} THEN NOW()+INTERVAL '5 minutes' ELSE NULL END,
                0,NULL,123,{Day.AddHours(9)},10,'Alta');
            INSERT INTO "MarketRapSnapshots" VALUES ({claim.ItemId},{Day.AddHours(9)},123);
            """);
        return claim;
    }

    private static async Task Sales(LiroDbContext db, Guid id, int days, decimal value)
    {
        for (var day = 1; day <= days; day++)
            await Point(db, id, "VolumenVentas", Day.AddDays(-day), value);
    }

    private static Task<int> Point(LiroDbContext db, Guid id, string metric, DateTime period, decimal value, string source = "marketplace-sales") =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MarketHistoryPoints" VALUES ({id},{source},{metric},{period},{value},{Day.AddHours(9)})
            """);

    private static Task<MarketWatch> Watch(LiroDbContext db) => db.Set<MarketWatch>().AsNoTracking().SingleAsync();

    private static async Task<string> Snapshot(LiroDbContext db, bool marketOnly = false)
    {
        var watch = await Watch(db);
        object data = marketOnly
            ? new { watch.RecentAveragePrice, watch.RapObservedAtUtc, watch.LastSuccessAtUtc, watch.Activity, watch.DailySales }
            : watch;
        return JsonSerializer.Serialize(new
        {
            Data = data,
            History = await db.Set<MarketHistoryPoint>().AsNoTracking().OrderBy(x => x.Source).ThenBy(x => x.Metric).ThenBy(x => x.PeriodUtc).ToArrayAsync(),
            Rap = await db.Set<MarketRapSnapshot>().AsNoTracking().OrderBy(x => x.ObservedAtUtc).ToArrayAsync()
        });
    }

    private sealed class FailAfterHistoryWrite(CancellationTokenSource? cancel) : SaveChangesInterceptor
    {
        public int RowsBeforeFailure { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RowsBeforeFailure = await data.Context!.Database.SqlQueryRaw<int>(
                """SELECT COUNT(*)::int AS "Value" FROM "MarketHistoryPoints" """).SingleAsync(cancellationToken);
            if (cancel is not null)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IntentionalSaveFailure();
        }
    }
}
