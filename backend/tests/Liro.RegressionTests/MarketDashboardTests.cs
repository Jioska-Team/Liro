using System.Text.Json;
using Liro.Application.Catalog.Services;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class MarketDashboardTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    public static void Register(RegressionSuite suite)
    {
        foreach (var row in new (int Up, int Down, int Same, string Expected)[] { (6, 4, 0, "Bullish"), (4, 6, 0, "Bearish"),
            (5, 5, 0, "Neutral"), (0, 0, 10, "Neutral"), (6, 4, 90, "Neutral"), (1, 0, 99, "Neutral") })
            suite.Test("MarketDashboard_Pulse_" + row.Up + "_" + row.Down + "_" + row.Same, () =>
            {
                var total = row.Up + row.Down + row.Same;
                var pulse = MarketDashboardCalculator.Pulse(row.Up, row.Down, row.Same, total, new() { MinimumPulseItems = 1 });
                Check.That(pulse.Status == row.Expected && pulse.AnalyzedItems == total
                    && pulse.BullishPercentage + pulse.BearishPercentage + pulse.NeutralPercentage == 100,
                    "Neutral observations distorted classification or breadth percentages");
                if (row.Up + row.Down == 0)
                    Check.That(pulse.DirectionalBullishPercentage is null && pulse.NeutralPercentage == 100, "Unchanged market was classified as falling");
            });
        suite.AsyncTest("MarketDashboard_ItemRapChangesUseFreshBoundedBaselines", async () =>
        {
            await using var db = await Open();
            await SummarySchema(db);
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Item(db, id, "Limited");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MarketWatches" ("ItemId","RecentAveragePrice","RapObservedAtUtc","Activity","DailySales") VALUES ({id},100,{now.AddMinutes(-2)},NULL,NULL)
                """);
            await Rap(db, id, now.AddHours(-1).AddMinutes(-2), 50);
            await Rap(db, id, now.AddHours(-24).AddMinutes(-2), 100);
            await Rap(db, id, now.AddDays(-30).AddMinutes(-2), 0);
            var repository = new ItemMarketSummaryRepository(db);
            var summary = await repository.GetAsync(id, default);
            Check.That(summary is { RapChange1hPercent: 100, RapChange24hPercent: 0, RapChange7dPercent: null,
                RapChange30dPercent: null, RapSource: "marketplace-sales", RapAgeSeconds: >= 120 },
                "Missing or zero baselines fabricated changes, or valid changes disappeared");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MarketWatches" SET "RapObservedAtUtc"={now.AddMinutes(-20)}
                """);
            summary = await repository.GetAsync(id, default);
            Check.That(summary?.RapChange1hPercent is null && summary?.RapChange24hPercent == 0,
                "One-hour changes accepted an old current observation");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MarketWatches" SET "RapObservedAtUtc"={now.AddHours(-4)}
                """);
            summary = await repository.GetAsync(id, default);
            Check.That(summary?.RapChange1hPercent is null && summary?.RapChange24hPercent is null,
                "Stale current RAP produced changes");
        });
        foreach (var scenario in new[] { "Complete", "Zero", "MissingDay", "Stale", "Ordinary", "PartialDay" })
            suite.AsyncTest("MarketSummary_VerifiedDailySales_" + scenario, async () =>
            {
                await using var db = await Open();
                await SummarySchema(db);
                var id = Guid.NewGuid();
                var now = DateTime.UtcNow;
                await Item(db, id, scenario == "Ordinary" ? "Normal" : "Limited");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "MarketWatches" ("ItemId","DailySales","LastSuccessAtUtc")
                    VALUES ({id},999,{(scenario == "Stale" ? now.AddHours(-4) : now.AddMinutes(-1))});
                    UPDATE "Items" SET "AssetTypeId"=8 WHERE "Id"={id};
                    INSERT INTO "ItemMarketState" ("ItemId","Data_TotalQuantity","Data_UnitsAvailableForConsumption","Data_FavoriteCount")
                    VALUES ({id},226891,0,NULL);
                    """);
                for (var day = 1; day <= (scenario == "MissingDay" ? 6 : 7); day++)
                    await Point(db, id, "VolumenVentas", now.Date.AddDays(-day),
                        scenario == "Zero" ? 0 : day, scenario == "PartialDay" && day == 1 ? now.Date.AddHours(-1) : now.AddMinutes(-2));
                var summary = await new ItemMarketSummaryRepository(db).GetAsync(id, default);
                var valid = scenario is "Complete" or "Zero";
                Check.That(summary!.DailySales == (valid ? scenario == "Zero" ? 0m : 4m : (decimal?)null),
                    "Missing, stale or unsupported data used cached sales, or valid zero disappeared");
                Check.That(summary.DailySalesWindowDays == 7 && summary.DailySalesPeriodStartUtc == now.Date.AddDays(-7)
                    && summary.DailySalesPeriodEndUtc == now.Date && summary.DailySalesObservedAtUtc.HasValue == valid,
                    "Sales window or provenance is wrong");
                Check.That(summary.AssetTypeId == 8 && summary.TotalQuantity == 226891
                    && summary.UnitsAvailableForConsumption == 0 && summary.FavoriteCount is null,
                    "Raw quantity was reinterpreted or zero/null counts were conflated");
            });
        suite.Test("MarketDashboard_MissingCoverageAndZeroBaselineStayUnknown", () =>
        {
            var options = new MarketDashboardOptions { MinimumPulseItems = 5, MinimumPulseCoveragePercent = 60 };
            Check.That(MarketDashboardCalculator.Pulse(4, 0, 0, 4, options).Status == "InsufficientData", "Minimum population ignored");
            Check.That(MarketDashboardCalculator.Pulse(5, 0, 0, 100, options).BullishPercentage is null, "Insufficient coverage looks valid");
            Check.That(MarketDashboardCalculator.Change(10, 0) is null && MarketDashboardCalculator.Change(0, 10) == -100
                && MarketDashboardCalculator.Coverage(0, 0) is null, "Unknown denominator became zero or divided by zero");
            Check.That(new MarketCapStatistic().Value is null && new MarketCapStatistic().Reason == "UnverifiedCirculatingSupply", "Unverified supply produced market cap");
        });
        suite.AsyncTest("MarketDashboard_StoredCountsPulsePairedDailyVolumeAndImmutableBuckets", async () =>
        {
            await using var db = await Open();
            var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in ids) await Item(db, id, id == ids[5] ? "Normal" : "Limited");
            for (var i = 0; i < 3; i++)
            {
                await Rap(db, ids[i], Now.AddDays(-1).AddMinutes(-10), 100);
                await Rap(db, ids[i], Now.AddMinutes(-10), i == 0 ? 110 : i == 1 ? 90 : 100);
            }
            // Zero old RAP cannot support a percent change; a future RAP must not be used.
            await Rap(db, ids[3], Now.AddDays(-1), 0);
            await Rap(db, ids[3], Now.AddMinutes(-10), 100);
            await Rap(db, ids[4], Now.AddDays(-1), 100);
            await Rap(db, ids[4], Now.AddMinutes(1), 200);
            await Pair(db, ids[0], Now.Date.AddDays(-1), 100, 3);
            await Pair(db, ids[0], Now.Date.AddDays(-2), 50, 2);
            await Pair(db, ids[1], Now.Date.AddDays(-1), 0, 0); // explicit zero, not missing
            await Pair(db, ids[5], Now.Date.AddDays(-1), 999, 999); // not Limited
            await Point(db, ids[2], "VolumenVentas", Now.Date.AddDays(-1), 100, Now.AddMinutes(-20));
            await Point(db, ids[2], "PrecioMedioVenta", Now.Date.AddDays(-1).AddHours(1), 100, Now.AddMinutes(-20));
            // Matching interval but differently observed price and volume is not a coherent pair.
            await Point(db, ids[3], "VolumenVentas", Now.Date.AddDays(-1), 100, Now.AddMinutes(-20));
            await Point(db, ids[3], "PrecioMedioVenta", Now.Date.AddDays(-1), 100, Now.AddMinutes(-30));
            // Snapshots, not edited FirstSeenAt or conversion dates, define weekly net growth.
            db.Set<MarketDashboardSnapshot>().Add(new() { BucketUtc = Now.AddDays(-7), LimitedsCount = 4, Payload = JsonSerializer.Serialize(MarketDashboardSummary.Empty) });
            await db.SaveChangesAsync();
            var repo = Repository(db);
            Check.That(await repo.CaptureAsync(Now, default), "Initial snapshot was not saved");
            var summary = await repo.GetLatestAsync(Now, default);
            Check.That(summary.Limiteds.Total == 5 && summary.Limiteds.WeeklyChange == 1, "Count or seven-day baseline is incorrect");
            Check.That(summary.MarketPulse.Status == "Neutral" && summary.MarketPulse.AnalyzedItems == 3
                && summary.MarketPulse.UpItems == 1 && summary.MarketPulse.DownItems == 1 && summary.MarketPulse.UnchangedItems == 1,
                "Pulse used invalid zero/future/stale data");
            Check.That(summary.Volume.Value == 300 && summary.Volume.IncludedItems == 2 && summary.Volume.CoveragePercent == 40
                && summary.Volume.Status == "Partial" && summary.Volume.IsEstimated && summary.Volume.ComparisonItems == 1
                && summary.Volume.ChangePercent == 200 && summary.Volume.PeriodStartUtc == Now.Date.AddDays(-1)
                && summary.Volume.PeriodEndUtc == Now.Date, "Daily volume mixed periods, missing metrics, universe or changing coverage");
            Check.That(summary.MarketCap.Value is null && !summary.IsStale, "Unknown market cap or fresh status is incorrect");
            await Item(db, Guid.NewGuid(), "LimitedUnique");
            Check.That(!await repo.CaptureAsync(Now.AddMinutes(5), default)
                && (await repo.GetLatestAsync(Now, default)).Limiteds.Total == 5, "Same snapshot bucket was recomputed");
            Check.That((await repo.GetLatestAsync(Now.AddHours(2), default)).IsStale, "Old snapshot was advertised as fresh");
        });
        foreach (var sample in new (string Name, long? Total, long? Remaining)[]
        {
            ("SoldOutInitialStock", 600, 0),
            ("UnsoldInitialStock", 600, 600),
            ("MissingInitialStock", null, 12),
            ("ExplicitZeroInitialStock", 0, 0)
        })
        suite.AsyncTest("MarketDashboard_CatalogQuantityDoesNotCertifyCirculation_" + sample.Name, async () =>
        {
            await using var db = await Open();
            await SummarySchema(db);
            var id = Guid.NewGuid();
            await Item(db, id, "LimitedUnique");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ItemMarketState" ("ItemId", "Data_TotalQuantity", "Data_UnitsAvailableForConsumption",
                    "Data_LowestResalePriceRobux", "Data_HasResellers", "ObservedAtUtc", "Source")
                VALUES ({id}, {sample.Total}, {sample.Remaining}, 12318, true, {Now}, 'RobloxCatalogDetails');
                INSERT INTO "MarketWatches" ("ItemId", "RecentAveragePrice", "RapObservedAtUtc", "LastSuccessAtUtc", "Activity")
                VALUES ({id}, 12318, {Now}, {Now}, 'Alta');
                """);
            await Rap(db, id, Now.AddMinutes(-1), 12318);
            await Pair(db, id, Now.Date.AddDays(-1), 12000, 3);
            var repository = Repository(db);
            await repository.CaptureAsync(Now, default);
            var dashboard = await repository.GetLatestAsync(Now, default);
            Check.That(dashboard.Limiteds.Total == 1 && dashboard.Volume.Value == 36000,
                "The fixture must have usable market observations, not an entirely missing market");
            Check.That(dashboard.MarketCap.Value is null && dashboard.MarketCap.Change24h is null
                && dashboard.MarketCap.CoveragePercent is null && dashboard.MarketCap.Status == "Unavailable"
                && dashboard.MarketCap.Reason == "UnverifiedCirculatingSupply",
                "Initial stock, remaining units or RAP were substituted for verified circulating supply");
            var itemSummary = await new ItemMarketSummaryRepository(db).GetAsync(id, default);
            Check.That(itemSummary?.TotalQuantity == sample.Total && itemSummary?.UnitsAvailableForConsumption == sample.Remaining,
                "Source quantities were erased or changed instead of retaining their original meaning");
        });
        suite.AsyncTest("MarketDashboard_EmptyHistoryDoesNotInventValuesOrWeeklyChange", async () =>
        {
            await using var db = await Open();
            var repo = Repository(db);
            var before = await repo.GetLatestAsync(Now, default);
            Check.That(before.UpdatedAt is null && before.Limiteds.Total is null, "No snapshot was presented as an empty market");
            await Item(db, Guid.NewGuid(), "LimitedUnique");
            await repo.CaptureAsync(Now, default);
            var summary = await repo.GetLatestAsync(Now, default);
            Check.That(summary.Limiteds.Total == 1 && summary.Limiteds.WeeklyChange is null && summary.Volume.Value is null
                && summary.Volume.ChangePercent is null && summary.MarketPulse.Status == "InsufficientData"
                && summary.MarketPulse.BullishPercentage is null, "Missing history became fabricated zeros");
        });
        suite.AsyncTest("MarketDashboard_StaleRAPAndIncompleteDayDoNotContribute", async () =>
        {
            await using var db = await Open();
            var id = Guid.NewGuid();
            await Item(db, id, "Limited");
            await Rap(db, id, Now.AddDays(-1).AddHours(-4), 100);
            await Rap(db, id, Now.AddMinutes(-1), 110);
            await Point(db, id, "PrecioMedioVenta", Now.Date.AddDays(-1), 100, Now.Date.AddHours(-1));
            await Point(db, id, "VolumenVentas", Now.Date.AddDays(-1), 2, Now.Date.AddHours(-1));
            var repo = Repository(db);
            await repo.CaptureAsync(Now, default);
            var summary = await repo.GetLatestAsync(Now, default);
            Check.That(summary.MarketPulse.AnalyzedItems == 0 && summary.Volume.Value is null, "Stale RAP or incomplete daily observation contributed");
        });
        suite.AsyncTest("MarketDashboard_CompletedUtcDayIgnoresSessionDst", async () =>
        {
            await using var db = await Open();
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'America/New_York'");
            var id = Guid.NewGuid();
            await Item(db, id, "Limited");
            // March 8 2026 jumps forward in New York. One local day from UTC midnight
            // lasts only 23 hours, but the UTC sale bucket still requires all 24 hours.
            var period = new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc);
            var observed = period.AddHours(23.5);
            await Point(db, id, "PrecioMedioVenta", period, 100, observed);
            await Point(db, id, "VolumenVentas", period, 2, observed);
            var repository = Repository(db);
            var now = period.AddDays(1).AddHours(12);
            await repository.CaptureAsync(now, default);
            var summary = await repository.GetLatestAsync(now, default);
            Check.That(summary.Volume.Value is null && summary.Volume.IncludedItems == 0,
                "A 23-hour DST calendar day certified an incomplete UTC sales bucket");
        });
        suite.AsyncTest("MarketDashboard_ZeroSalesAreRealZeroNotUnavailable", async () =>
        {
            await using var db = await Open();
            var id = Guid.NewGuid();
            await Item(db, id, "Limited");
            await Pair(db, id, Now.Date.AddDays(-1), 100, 0);
            await Pair(db, id, Now.Date.AddDays(-2), 100, 0);
            var repo = Repository(db);
            await repo.CaptureAsync(Now, default);
            var summary = await repo.GetLatestAsync(Now, default);
            Check.That(summary.Volume.Value == 0 && summary.Volume.Status == "Available" && summary.Volume.ChangePercent is null,
                "Explicit zero sales became missing data or a fabricated percent change");
        });
    }
    private static MarketDashboardRepository Repository(LiroDbContext db) =>
        new(db, Options.Create(new MarketDashboardOptions { MinimumPulseItems = 3, MinimumPulseCoveragePercent = 50 }));
    private static Task SummarySchema(LiroDbContext db) => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Items" ADD "RobloxAssetId" bigint DEFAULT 1, ADD "ItemType" text DEFAULT 'Asset',
            ADD "Name" text DEFAULT 'Test', ADD "MarketplaceUrl" text DEFAULT '', ADD "ThumbnailUrl" text, ADD "AssetTypeId" integer;
        CREATE TEMP TABLE "ItemMarketState" ("ItemId" uuid, "Data_LowestResalePriceRobux" bigint,
            "Data_HasResellers" boolean, "ObservedAtUtc" timestamptz, "Source" text,
            "Data_TotalQuantity" bigint,"Data_UnitsAvailableForConsumption" bigint,"Data_FavoriteCount" bigint);
        CREATE TEMP TABLE "MarketWatches" ("ItemId" uuid, "RecentAveragePrice" numeric,
            "RapObservedAtUtc" timestamptz, "Activity" text, "DailySales" numeric,"LastSuccessAtUtc" timestamptz);
        """);
    private static async Task<LiroDbContext> Open()
    {
        var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>()
            .UseNpgsql(IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES")).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            SET TIME ZONE 'America/Guayaquil';
            CREATE TEMP TABLE "Items" ("Id" uuid PRIMARY KEY,"MarketStatus" text);
            CREATE TEMP TABLE "MarketRapSnapshots" ("ItemId" uuid,"ObservedAtUtc" timestamptz,"Value" numeric,PRIMARY KEY("ItemId","ObservedAtUtc"));
            CREATE TEMP TABLE "MarketHistoryPoints" ("ItemId" uuid,"Source" text,"Metric" text,"PeriodUtc" timestamptz,"Value" numeric,"ObservedAtUtc" timestamptz,
                PRIMARY KEY("ItemId","Source","Metric","PeriodUtc"));
            CREATE TEMP TABLE "MarketDashboardSnapshots" ("BucketUtc" timestamptz PRIMARY KEY,"LimitedsCount" integer NOT NULL,"Payload" jsonb NOT NULL);
            """);
        return db;
    }
    private static Task Item(LiroDbContext db, Guid id, string status) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Items" ("Id","MarketStatus") VALUES({id},{status})""");
    private static Task Rap(LiroDbContext db, Guid id, DateTime observed, decimal value) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MarketRapSnapshots" VALUES({id},{observed},{value})""");
    private static Task Point(LiroDbContext db, Guid id, string metric, DateTime period, decimal value, DateTime observed) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MarketHistoryPoints" VALUES({id},'marketplace-sales',{metric},{period},{value},{observed})""");
    private static async Task Pair(LiroDbContext db, Guid id, DateTime period, decimal price, decimal quantity)
    {
        await Point(db, id, "PrecioMedioVenta", period, price, Now.AddMinutes(-20));
        await Point(db, id, "VolumenVentas", period, quantity, Now.AddMinutes(-20));
    }
}
