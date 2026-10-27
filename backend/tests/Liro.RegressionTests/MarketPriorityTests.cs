using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class MarketPriorityTests
{
    public static void Register(RegressionSuite suite)
    {
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        suite.AsyncTest("MarketPriority_VeryActiveBeatsOlderHighBacklog", async () =>
        {
            await using var db = await Open(connection);
            var hot = await Add(db, "MuyAlta", 16);
            var high = await Add(db, "Alta", 60);
            var cold = await Add(db, "SinHistorial", 90);
            var store = new MarketStore(db);
            var plan = Plan();
            Check.That((await store.ClaimAsync(default, false, plan))?.ItemId == hot, "High backlog displaced due very-active item");
            Check.That((await store.ClaimAsync(default, true, plan))?.ItemId == cold, "Background turn starved");
            Check.That((await store.ClaimAsync(default, false, plan))?.ItemId == high, "High activity did not progress");
            Check.That(await store.ClaimAsync(default, false, plan) is null, "A leased item was claimed twice");
        });
        suite.AsyncTest("MarketPriority_RespectsDueTimeAndProviderDelay", async () =>
        {
            await using var db = await Open(connection);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var hot = await Add(db, "MuyAlta", 14);
            var high = await Add(db, "Alta", 60);
            var store = new MarketStore(db);
            Check.That((await store.ClaimAsync(default, false, Plan()))?.ItemId == high, "Very-active item queried before its interval");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MarketWatches" SET "LastSuccessAtUtc"=NOW()-INTERVAL '15 minutes',
                  "LastErrorCode"='HTTP429', "NextCheckAtUtc"=NOW()+INTERVAL '2 minutes' WHERE "ItemId"={hot}
                """);
            Check.That(await store.ClaimAsync(default, false, Plan()) is null, "Priority bypassed provider delay");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MarketWatches" SET "LastErrorCode"=NULL WHERE "ItemId"={hot}
                """);
            Check.That((await store.ClaimAsync(default, false, Plan()))?.ItemId == hot, "Item at the 15-minute boundary was not eligible");
        });
        suite.AsyncTest("MarketPriority_FiveHotItemsKeepCadenceAndBackgroundAdvances", async () =>
        {
            await using var db = await Open(connection);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var hot = new HashSet<Guid>();
            for (var i = 0; i < 5; i++)
                hot.Add(await Add(db, "MuyAlta", 30 + i));
            for (var i = 0; i < 72; i++)
                await Add(db, "Alta", 31);
            var background = new HashSet<Guid>();
            for (var i = 0; i < 57; i++)
                background.Add(await Add(db, "SinHistorial", 31));
            var store = new MarketStore(db);
            var last = new Dictionary<Guid, int>();
            var repeats = new Dictionary<Guid, int>();
            var highCount = 0;
            var backgroundCount = 0;
            // This scenario isolates activity cadence: all observations stay under the 180-minute
            // coverage deadline. Overdue coverage priority is exercised by MarketCoverage tests.
            // Simulated 65-second budget turns, no HTTP requests or real waiting.
            for (var slot = 0; slot < 120; slot++)
            {
                var claim = await store.ClaimAsync(default, slot % 4 == 3, Plan());
                Check.That(claim is not null, "Backlog unexpectedly stopped advancing");
                var id = claim!.ItemId;
                if (hot.Contains(id))
                {
                    if (last.TryGetValue(id, out var previous))
                    {
                        Check.That((slot - previous) * 65 <= 1040, "Very-active cadence exceeded 17m20s despite available capacity");
                        repeats[id] = repeats.GetValueOrDefault(id) + 1;
                    }
                    last[id] = slot;
                }
                else if (background.Contains(id))
                    backgroundCount++;
                else
                    highCount++;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "MarketWatches" SET "LastSuccessAtUtc"=NOW(), "LeaseId"=NULL, "LeaseUntilUtc"=NULL
                    WHERE "ItemId"={id} AND "LeaseId"={claim.LeaseId}
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "MarketWatches" SET "LastSuccessAtUtc"="LastSuccessAtUtc"-INTERVAL '65 seconds'
                    """);
            }
            Check.That(hot.All(id => repeats.GetValueOrDefault(id) >= 6), "A very-active item was starved");
            Check.That(backgroundCount >= 30 && highCount > 0, "Priority stopped the remaining queue");
        });
    }

    private static MarketOptions Plan() => new() { VeryActiveMinutes = 15, ActiveMinutes = 30, MissingHistoryMinutes = 30 };

    private static async Task<LiroDbContext> Open(string connection)
    {
        var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE "Items" ("Id" uuid PRIMARY KEY, "CreatorId" bigint, "CreatorType" text, "MarketStatus" text, "CollectibleItemId" uuid);
            CREATE TEMP TABLE "MarketWatches" ("ItemId" uuid PRIMARY KEY, "Activity" text,
              "LastSuccessAtUtc" timestamptz, "NextCheckAtUtc" timestamptz, "LastErrorCode" text,
              "LeaseId" uuid, "LeaseUntilUtc" timestamptz, "LastAttemptAtUtc" timestamptz);
            """);
        return db;
    }

    private static async Task<Guid> Add(LiroDbContext db, string activity, int minutesAgo)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Items" VALUES ({id},1,'User','Limited',{Guid.NewGuid()});
            INSERT INTO "MarketWatches" ("ItemId","Activity","LastSuccessAtUtc","NextCheckAtUtc")
              VALUES ({id},{activity},NOW()-INTERVAL '1 minute'*{minutesAgo},NOW()-INTERVAL '1 day');
            """);
        return id;
    }
}
