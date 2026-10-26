using System.Net;
using System.Text.Json;
using Liro.Api;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Liro.RegressionTests;

internal static class CatalogQueryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("CatalogLatest_HistoricalOrderMigrationLeavesDatesUnknown", async () =>
        {
            await using var host = await Host.Start();
            var before = await host.Db.Items.ToDictionaryAsync(x => x.Id,
                x => new { x.FirstSeenAt, x.BecameLimitedAtUtc, x.LimitedFirstObservedAtUtc, x.MarketStatusObservedAtUtc });
            await host.Db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP COLUMN \"LimitedHistoricalOrderAtUtc\"");
            var migration = new Liro.Infrastructure.Persistence.Migrations.AddLimitedHistoricalOrder();
            var commands = host.Db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, host.Db.Model);
            foreach (var command in commands)
                await host.Db.Database.ExecuteSqlRawAsync(command.CommandText);
            host.Db.ChangeTracker.Clear();
            var after = await host.Db.Items.ToArrayAsync();
            Check.That(after.Length == before.Count && after.All(x => x.LimitedHistoricalOrderAtUtc is null
                && x.FirstSeenAt == before[x.Id].FirstSeenAt && x.BecameLimitedAtUtc == before[x.Id].BecameLimitedAtUtc
                && x.LimitedFirstObservedAtUtc == before[x.Id].LimitedFirstObservedAtUtc
                && x.MarketStatusObservedAtUtc == before[x.Id].MarketStatusObservedAtUtc),
                "Adding manual order metadata must not seed artificial dates or change observations");
            Check.That(!host.Db.Database.HasPendingModelChanges(), "Historical order migration and EF model differ");
        });
        suite.AsyncTest("CatalogLatest_ManualHistoricalOrderPreservesObservedDates", async () =>
        {
            await using var host = await Host.Start();
            var baseline = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = await host.Db.Items.OrderBy(x => x.RobloxAssetId).ToArrayAsync();
            foreach (var row in rows)
                host.Db.Entry(row).Property(x => x.FirstSeenAt).CurrentValue = baseline.AddDays(row.RobloxAssetId);
            var converted = new Item(800, "Confirmed conversion", marketStatus: ItemMarketStatus.Normal, observedAtUtc: baseline);
            converted.UpdateDetails(new ItemCatalogDetails { CreatorId = 1, CreatorType = "User", AssetTypeId = 8 });
            converted.UpdateCatalogData(converted.Name, null, ItemMarketStatus.Limited, baseline.AddDays(1));
            host.Db.Items.Add(converted);
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            using var before = await host.Json("/latest");
            var originalArrivals = before.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
            var originalDates = await host.Db.Items.ToDictionaryAsync(x => x.Id,
                x => new { x.FirstSeenAt, x.BecameLimitedAtUtc, x.LimitedFirstObservedAtUtc, x.MarketStatusObservedAtUtc });
            // The manual dates are ordering keys only; ties use the internal ID. A real conversion stays first.
            await host.Db.Items.Where(x => x.RobloxAssetId == 1 || x.RobloxAssetId == 2)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LimitedHistoricalOrderAtUtc, baseline.AddMonths(6)));
            await host.Db.Items.Where(x => x.RobloxAssetId == 3)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LimitedHistoricalOrderAtUtc, baseline.AddMonths(5)));
            // Manual keys cannot make a normal, foreign, or hidden component appear as a visible Limited.
            await host.Db.Items.Where(x => x.RobloxAssetId == 4 || x.RobloxAssetId == 9 || x.RobloxAssetId == 10)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LimitedHistoricalOrderAtUtc, baseline.AddYears(1)));
            host.Db.ChangeTracker.Clear();
            using var ordered = await host.Json("/latest");
            long[] LimitedIds(JsonDocument page) => page.RootElement.GetProperty("limiteds").EnumerateArray()
                .Select(x => x.GetProperty("robloxItemId").GetInt64()).ToArray();
            var expected = new long[] { 800, 1, 2, 3, 8, 7, 5 };
            Check.That(LimitedIds(ordered).SequenceEqual(expected),
                "Manual historical order must follow confirmed conversions and precede undated imports, with stable ties");
            Check.That(ordered.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).SequenceEqual(originalArrivals),
                "Manual historical keys changed the recent arrivals order");
            Check.That(!ordered.RootElement.GetProperty("limiteds").EnumerateArray().Any(x => x.TryGetProperty("limitedHistoricalOrderAtUtc", out _)),
                "Manual presentation metadata leaked into the public card contract");
            foreach (var row in await host.Db.Items.ToArrayAsync())
            {
                var original = originalDates[row.Id];
                Check.That(row.FirstSeenAt == original.FirstSeenAt && row.BecameLimitedAtUtc == original.BecameLimitedAtUtc
                    && row.LimitedFirstObservedAtUtc == original.LimitedFirstObservedAtUtc && row.MarketStatusObservedAtUtc == original.MarketStatusObservedAtUtc,
                    "Manual ordering fabricated or changed a registration, conversion, or observation date");
            }
            var renamed = await host.Db.Items.SingleAsync(x => x.RobloxAssetId == 3);
            renamed.UpdateCatalogData("Renamed historical Limited", null, renamed.MarketStatus, baseline.AddYears(2));
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            using var refreshed = await host.Json("/latest");
            Check.That(LimitedIds(refreshed).SequenceEqual(expected)
                && await host.Db.Items.Where(x => x.RobloxAssetId == 3).Select(x => x.LimitedHistoricalOrderAtUtc).SingleAsync() == baseline.AddMonths(5)
                && await host.Db.OutboxMessages.CountAsync() == 1,
                "Refreshing metadata lost manual order or emitted a fabricated conversion event");
        });
        suite.AsyncTest("CatalogLatest_ThirtyOwnRegistrationsAndConfirmedConversions", async () =>
        {
            await using var host = await Host.Start();
            using var initial = await host.Json("/latest");
            Check.That(initial.RootElement.GetProperty("items").GetArrayLength() == 8
                && initial.RootElement.GetProperty("limiteds").GetArrayLength() == 6,
                "Latest endpoint lost official catalog visibility or returned filler rows");
            var baseline = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await host.Db.Items.ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstSeenAt, baseline));
            for (var i = 0; i < 39; i++)
            {
                var limited = i < 35 || i >= 37;
                var item = new Item(100 + i, "Recent " + i,
                    marketStatus: limited ? ItemMarketStatus.LimitedUnique : ItemMarketStatus.Normal);
                host.Db.Entry(item).Property(x => x.Id).CurrentValue = Guid.Parse("00000000-0000-0000-0000-" + (100 + i).ToString("D12"));
                // Creation order deliberately opposes Liro registration order.
                item.UpdateDetails(new ItemCatalogDetails { CreatorId = i == 37 ? 2 : 1, CreatorType = "User",
                    AssetTypeId = i == 38 ? 28 : 8, CreatedAtUtc = baseline.AddDays(100 - i) });
                host.Db.Entry(item).Property(x => x.FirstSeenAt).CurrentValue = baseline.AddDays(i + 1);
                // Equal conversion dates exercise the stable ID tie break.
                if (i < 3)
                    host.Db.Entry(item).Property("BecameLimitedAtUtc").CurrentValue = baseline.AddDays(i == 2 ? 95 : 90);
                // A free listing is available, unlike a missing price.
                if (i == 36)
                    item.ObserveMarket(new ItemMarketData { PrimaryPriceRobux = 0, IsOffSale = false }, baseline, "latest-test");
                host.Db.Items.Add(item);
            }
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            host.Sql.Clear();
            using var page = await host.Json("/latest");
            var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var limiteds = page.RootElement.GetProperty("limiteds").EnumerateArray().ToArray();
            long[] ReadIds(JsonElement[] rows) => rows.Select(x => x.GetProperty("robloxItemId").GetInt64()).ToArray();
            Check.That(ReadIds(items).SequenceEqual(Enumerable.Range(107, 30).Reverse().Select(x => (long)x)),
                "Latest arrivals must be the 30 newest Liro registrations, not Roblox creation or hidden items");
            Check.That(ReadIds(limiteds).SequenceEqual(new long[] { 102, 100, 101 }.Concat(Enumerable.Range(108, 27).Reverse().Select(x => (long)x))),
                "Latest Limiteds must prioritize detected conversions then use registration only for undated imports");
            Check.That(items[0].GetProperty("currentPriceRobux").GetInt64() == 0
                && items[1].GetProperty("currentPriceRobux").ValueKind == JsonValueKind.Null,
                "Latest endpoint must distinguish zero price from unavailable price");
            Check.That(!limiteds.Any(x => x.TryGetProperty("becameLimitedAtUtc", out _)),
                "Internal detection timestamps must stay out of the public card payload");
            Check.That(host.Sql.Count(x => x.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)) >= 2,
                "Latest collections must be bounded in the database");
            await host.Db.Items.Where(x => x.RobloxAssetId == 134)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUpdatedAt, baseline.AddDays(200)));
            using var updated = await host.Json("/latest");
            Check.That(updated.RootElement.GetRawText() != page.RootElement.GetRawText(), "Fixture metadata update did not occur");
            Check.That(updated.RootElement.GetProperty("limiteds").EnumerateArray().Select(x => x.GetProperty("robloxItemId").GetInt64())
                .SequenceEqual(ReadIds(limiteds)), "Unrelated metadata update reordered Limiteds");
            foreach (var item in await host.Db.Items.Where(x => x.RobloxAssetId >= 100 && x.RobloxAssetId <= 102).ToArrayAsync())
                host.Db.Entry(item).Property("BecameLimitedAtUtc").CurrentValue = null;
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            using var baselineOnly = await host.Json("/latest");
            Check.That(baselineOnly.RootElement.GetProperty("limiteds").EnumerateArray().Select(x => x.GetProperty("robloxItemId").GetInt64())
                .SequenceEqual(Enumerable.Range(105, 30).Reverse().Select(x => (long)x)),
                "Thirty historical Limiteds must remain available without inventing conversion dates");
        });
        suite.AsyncTest("LimitedConversion_PersistsObservationAndSingleOutboxEvent", async () =>
        {
            await using var host = await Host.Start();
            var observed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var converted = new Item(500, "Normal", marketStatus: ItemMarketStatus.Normal, observedAtUtc: observed);
            var importedLimited = new Item(501, "Imported Limited", marketStatus: ItemMarketStatus.Limited, observedAtUtc: observed);
            var unknown = new Item(502, "Unknown", observedAtUtc: observed);
            host.Db.Items.AddRange(converted, importedLimited, unknown);
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            var recorded = await host.Db.Items.SingleAsync(x => x.RobloxAssetId == 500);
            var registeredAt = recorded.FirstSeenAt;
            recorded.UpdateCatalogData("Converted", null, ItemMarketStatus.Limited, observed.AddDays(1));
            var formerlyUnknown = await host.Db.Items.SingleAsync(x => x.RobloxAssetId == 502);
            formerlyUnknown.UpdateCatalogData("Discovered", null, ItemMarketStatus.Limited, observed.AddDays(1));
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            recorded = await host.Db.Items.SingleAsync(x => x.RobloxAssetId == 500);
            Check.That(recorded.BecameLimitedAtUtc == observed.AddDays(1)
                && recorded.LimitedFirstObservedAtUtc == observed.AddDays(1)
                && recorded.MarketStatusObservedAtUtc == observed.AddDays(1)
                && recorded.FirstSeenAt == registeredAt,
                "Reload lost the observed conversion date or changed registration time");
            Check.That(await host.Db.Items.CountAsync(x => x.RobloxAssetId >= 501 && x.RobloxAssetId <= 502
                && x.BecameLimitedAtUtc == null && x.LimitedFirstObservedAtUtc != null) == 2,
                "Initial imports or Unknown classifications acquired invented conversion dates");
            var message = await host.Db.OutboxMessages.SingleAsync();
            Check.That(message.Type == "catalog.item-became-limited.v1" && message.OccurredAtUtc == observed.AddDays(1),
                "Confirmed conversion and outbox timestamp were not persisted together");
            recorded.UpdateCatalogData("Converted", null, ItemMarketStatus.Limited, observed.AddDays(2));
            Check.That(!recorded.UpdateCatalogData("Stale normal", null, ItemMarketStatus.Normal, observed),
                "Stale observation replaced a persisted conversion");
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();
            recorded = await host.Db.Items.SingleAsync(x => x.RobloxAssetId == 500);
            Check.That(recorded.MarketStatus == ItemMarketStatus.Limited && recorded.Name == "Converted"
                && recorded.BecameLimitedAtUtc == observed.AddDays(1)
                && recorded.MarketStatusObservedAtUtc == observed.AddDays(2)
                && await host.Db.OutboxMessages.CountAsync() == 1,
                "Repeated or stale observations changed the conversion date or duplicated its event");
        });
        suite.AsyncTest("LimitedConversion_MigrationPreservesUnknownHistoricalDates", async () =>
        {
            await using var host = await Host.Start();
            var registered = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await host.Db.Items.ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstSeenAt, registered));
            // Reproduce the predecessor schema inside this test's disposable schema only.
            await host.Db.Database.ExecuteSqlRawAsync("""
                DROP INDEX "IX_Items_FirstSeenAt_Id";
                DROP INDEX "IX_Items_BecameLimitedAtUtc_FirstSeenAt_Id";
                ALTER TABLE "Items" DROP COLUMN "BecameLimitedAtUtc", DROP COLUMN "HasBeenLimited",
                    DROP COLUMN "LimitedFirstObservedAtUtc", DROP COLUMN "MarketStatusObservedAtUtc";
                """);
            Check.That(!host.Db.Database.HasPendingModelChanges(), "Limited detection model and migration snapshot differ");
            var migration = new Liro.Infrastructure.Persistence.Migrations.TrackLimitedConversions();
            var commands = host.Db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, host.Db.Model);
            foreach (var command in commands)
                await host.Db.Database.ExecuteSqlRawAsync(command.CommandText);
            host.Db.ChangeTracker.Clear();
            var items = await host.Db.Items.ToArrayAsync();
            Check.That(items.Length == 10 && items.All(x => x.FirstSeenAt == registered
                && x.BecameLimitedAtUtc == null && x.LimitedFirstObservedAtUtc == null && x.MarketStatusObservedAtUtc == null),
                "Migration fabricated historical dates or changed registration times");
            Check.That(items.All(x => x.HasBeenLimited == (x.MarketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)),
                "Migration did not preserve known Limited membership separately from unknown dates");
        });
        suite.AsyncTest("CatalogLatest_EmptyCatalogHasNoFabricatedRows", async () =>
        {
            await using var host = await Host.Start();
            await host.Db.Items.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatorId, 2));
            using var page = await host.Json("/latest");
            Check.That(page.RootElement.GetProperty("items").GetArrayLength() == 0
                && page.RootElement.GetProperty("limiteds").GetArrayLength() == 0,
                "Empty collections should not fabricate data or use a third-party fallback");
        });
        suite.Test("CatalogQueries_CursorCodecTypedKeysAndBounds", () =>
        {
            var id = Guid.NewGuid();
            foreach (var (sort, cursor) in new[]
            {
                (CatalogSort.Created, new CatalogCursor(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560), id)),
                (CatalogSort.Created, new CatalogCursor(null, id)),
                (CatalogSort.Name, new CatalogCursor(null, id, Name: string.Concat(Enumerable.Repeat("😀", 200)))),
                (CatalogSort.Price, new CatalogCursor(null, id, Value: long.MaxValue)),
                (CatalogSort.Rap, new CatalogCursor(null, id, Value: 1234567890123456789012.123456m)),
                (CatalogSort.Sales, new CatalogCursor(null, id, Value: 0)),
                (CatalogSort.Sales, new CatalogCursor(null, id))
            })
            {
                var options = new CatalogBrowseOptions(CatalogLimitedFilter.Limiteds, "帽子", sort);
                var token = Liro.Api.Contracts.CatalogCursorCodec.Encode(cursor, options);
                Check.That(Liro.Api.Contracts.CatalogCursorCodec.TryDecode(token, options, out var decoded) && cursor == decoded,
                    "Cursor lost sort key, Unicode, or decimal precision");
                Check.That(!Liro.Api.Contracts.CatalogCursorCodec.TryDecode(token, options with { Descending = false }, out _),
                    "Cursor accepted changed ordering");
                Check.That(!Liro.Api.Contracts.CatalogCursorCodec.TryDecode(token + "=", options, out _),
                    "Noncanonical cursor accepted");
            }
            var nameOptions = new CatalogBrowseOptions(Sort: CatalogSort.Name);
            foreach (var invalid in new[]
            {
                new CatalogCursor(null, Guid.Empty, Name: "Name"), new CatalogCursor(null, id),
                new CatalogCursor(null, id, Name: "a\0b"), new CatalogCursor(null, id, Name: new string('x', 401)),
                new CatalogCursor(null, id, Name: "Name", Value: 1)
            })
                Check.That(!Liro.Api.Contracts.CatalogCursorCodec.TryDecode(Liro.Api.Contracts.CatalogCursorCodec.Encode(invalid, nameOptions), nameOptions, out _),
                    "Invalid name position accepted");
            Check.That(!Liro.Api.Contracts.CatalogCursorCodec.TryDecode(new string('a', 10000), nameOptions, out _),
                "Oversized cursor accepted");
        });
        suite.AsyncTest("CatalogQueries_PricesAndLimitedRapAcrossBrowseRoutes", async () =>
        {
            await using var host = await Host.Start();
            foreach (var route in new[]
            {
                "?pageSize=100", "/feed?pageSize=100", "?sort=price&direction=asc&pageSize=100",
                "/feed?sort=rap&direction=desc&pageSize=100", "?limited=limiteds&q=alpha",
                "/feed?limited=limiteds&q=alpha"
            })
            {
                host.Db.ChangeTracker.Clear();
                using var page = await host.Json(route);
                var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Check.That(items.Length == (route.Contains("q=alpha") ? 2 : 8), "Price assertions need the complete expected page: " + route);
                foreach (var item in items)
                {
                    var i = checked((int)item.GetProperty("robloxAssetId").GetInt64() - 1);
                    var rap = Statuses[i] is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique ? Raps[i] : null;
                    AssertPrices(item, OriginalPrices[i], Prices[i], rap, route);
                }
            }
        });
        suite.AsyncTest("CatalogQueries_ComponentAndParentPricesUsePersistedMarketData", async () =>
        {
            await using var host = await Host.Start();
            var observedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
            var bundle = new Item(900, "Discounted bundle", marketStatus: ItemMarketStatus.Normal, itemType: CatalogItemType.Bundle);
            bundle.UpdateBundleComponents([1, 2, 7, 999], observedAt);
            bundle.ObserveMarket(new ItemMarketData { PrimaryPriceRobux = 40000, PriceBeforeDiscountRobux = 50000, IsOffSale = false }, observedAt, "catalog-query-test");
            host.Db.Items.Add(bundle);
            host.Db.Set<MarketWatch>().Add(new MarketWatch { ItemId = bundle.Id, NextCheckAtUtc = observedAt, RecentAveragePrice = 999 });
            await host.Db.SaveChangesAsync();
            host.Db.ChangeTracker.Clear();

            using var contents = await host.Json("/bundles/900/components");
            AssertPrices(contents.RootElement.GetProperty("bundle"), 50000, 40000, null, "bundle");
            var components = contents.RootElement.GetProperty("components").EnumerateArray().ToArray();
            Check.That(components.Select(x => x.GetProperty("robloxAssetId").GetInt64()).SequenceEqual(new long[] { 1, 2, 7 }),
                "Price mapping changed component membership");
            foreach (var component in components)
            {
                var i = checked((int)component.GetProperty("robloxAssetId").GetInt64() - 1);
                AssertPrices(component, OriginalPrices[i], Prices[i], Raps[i], "component");
            }
            Check.That(contents.RootElement.GetProperty("pendingAssetIds").EnumerateArray().Select(x => x.GetInt64()).SequenceEqual(new long[] { 999 }),
                "Price mapping changed pending component IDs");
            host.Db.ChangeTracker.Clear();
            using var parents = await host.Json("/assets/1/bundles");
            var parent = parents.RootElement.EnumerateArray().Single();
            Check.That(parent.GetProperty("id").GetGuid() == bundle.Id, "Wrong parent returned");
            AssertPrices(parent, 50000, 40000, null, "parent");
        });
        suite.AsyncTest("CatalogQueries_LimitedFiltersBeforePagination", async () =>
        {
            await using var host = await Host.Start();
            foreach (var (filter, expected) in new[]
            {
                ("limited", new long[] { 1, 3, 7 }),
                ("limitedUnique", new long[] { 2, 5, 8 }),
                ("limiteds", new long[] { 1, 2, 3, 5, 7, 8 })
            })
            {
                using var page = await host.Json("?limited=" + filter + "&pageSize=100");
                Check.That(Ids(page).SequenceEqual(expected), "Limited filter leaked another status or lost catalog visibility");
                using var second = await host.Json("?limited=" + filter + "&pageSize=1&page=2");
                Check.That(Ids(second).SequenceEqual(expected.Skip(1).Take(1))
                    && second.RootElement.GetProperty("hasMore").GetBoolean(), "Filtering occurred after pagination");
                var traversed = await host.Traverse("limited=" + filter, 1);
                Check.That(traversed.SequenceEqual(expected), "Filtered feed differs from offset results");
            }
        });
        suite.AsyncTest("CatalogQueries_SearchLiteralNameAndExactId", async () =>
        {
            await using var host = await Host.Start();
            foreach (var (search, expected) in new[]
            {
                (" aLpHa ", new long[] { 2, 3, 6 }), ("2", new long[] { 2 }),
                ("%", new long[] { 5 }), ("_", new long[] { 5 }), ("\\", new long[] { 5 }),
                ("absent", Array.Empty<long>()), ("9223372036854775808", Array.Empty<long>()),
                ("' OR 1=1 --", Array.Empty<long>())
            })
            {
                using var page = await host.Json("?q=" + Uri.EscapeDataString(search));
                Check.That(Ids(page).SequenceEqual(expected), "Search must match literal case-insensitive names or exact IDs: " + search);
                Check.That(!page.RootElement.GetProperty("hasMore").GetBoolean(), "Search produced a false next page");
            }
            using var combined = await host.Json("?q=ALPHA&limited=limiteds&sort=name&direction=asc");
            Check.That(Ids(combined).SequenceEqual(new long[] { 2, 3 }), "Search and Limited filter were not combined");
            using var empty = await host.Json("?q=" + new string('x', 100));
            Check.That(Ids(empty).Length == 0, "Maximum search length must be accepted");
        });
        foreach (var sort in new[] { "created", "name", "price", "rap", "sales" })
        foreach (var direction in new[] { "asc", "desc" })
            suite.AsyncTest("CatalogQueries_OrderAndCursor_" + sort + "_" + direction, async () =>
            {
                await using var host = await Host.Start();
                var expected = Expected(sort, direction == "desc");
                var query = "sort=" + sort + "&direction=" + direction;
                using var page = await host.Json("?" + query + "&pageSize=100");
                Check.That(Ids(page).SequenceEqual(expected), "Incorrect order, tie break, zero handling, or null placement: " + query);
                using var unchanged = await host.Json("?pageSize=100");
                var originalItems = unchanged.RootElement.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetRawText());
                Check.That(page.RootElement.GetProperty("items").EnumerateArray().All(x => originalItems[x.GetProperty("id").GetGuid()] == x.GetRawText()),
                    "Sorting changed item payloads or exposed internal metadata");
                using var second = await host.Json("?" + query + "&page=2&pageSize=2");
                Check.That(Ids(second).SequenceEqual(expected.Skip(2).Take(2)), "Offset sort differs from whole result");
                host.Sql.Clear();
                var traversed = await host.Traverse(query, 1);
                Check.That(traversed.SequenceEqual(expected), "Feed skipped or repeated rows at ties or nulls: " + query);
                Check.That(!host.Sql.Any(x => x.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)), "Feed used OFFSET");
                Check.That(host.Sql.Any(x => x.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)), "Pagination was not bounded in SQL");
            });
        suite.AsyncTest("CatalogQueries_DefaultCompatibilityAndCursorScope", async () =>
        {
            await using var host = await Host.Start();
            using var legacy = await host.Json("?pageSize=2");
            using var explicitDefaults = await host.Json("?sort=created&direction=desc&q=%20&pageSize=2");
            Check.That(legacy.RootElement.GetRawText() == explicitDefaults.RootElement.GetRawText(), "Default options changed legacy response fields");
            using var feed = await host.Json("/feed?pageSize=2");
            var oldCursor = feed.RootElement.GetProperty("nextCursor").GetString()!;
            Check.That(Liro.Api.Contracts.CatalogCursorCodec.TryDecode(oldCursor, out _), "Default feed stopped producing legacy cursors");
            using var continued = await host.Json("/feed?pageSize=2&after=" + oldCursor);
            Check.That(Ids(continued).SequenceEqual(new long[] { 3, 4 }), "Existing cursor stopped working");
            using var filtered = await host.Json("/feed?limited=limiteds&q=alpha&sort=price&direction=asc&pageSize=1");
            var cursor = filtered.RootElement.GetProperty("nextCursor").GetString()!;
            using var next = await host.Json("/feed?limited=limiteds&q=alpha&sort=price&direction=asc&pageSize=2&after=" + cursor);
            Check.That(Ids(next).SequenceEqual(new long[] { 3 }), "Page size must be changeable without changing cursor scope");
            foreach (var query in new[]
            {
                "limited=limited&q=alpha&sort=price&direction=asc",
                "limited=limiteds&q=other&sort=price&direction=asc",
                "limited=limiteds&q=alpha&sort=rap&direction=asc",
                "limited=limiteds&q=alpha&sort=price&direction=desc", ""
            })
                await host.Bad("/feed?" + query + "&after=" + cursor);
            await host.Bad("/feed?limited=limiteds&after=" + oldCursor);
            // Cursor positions remain valid if the anchor row disappears.
            var anchorId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            await host.Db.Set<ItemMarketSnapshot>().Where(x => x.ItemId == anchorId).ExecuteDeleteAsync();
            await host.Db.Set<ItemMarketState>().Where(x => x.ItemId == anchorId).ExecuteDeleteAsync();
            await host.Db.Items.Where(x => x.Id == anchorId).ExecuteDeleteAsync();
            using var deleted = await host.Json("/feed?limited=limiteds&q=alpha&sort=price&direction=asc&pageSize=2&after=" + cursor);
            Check.That(Ids(deleted).SequenceEqual(new long[] { 3 }), "Cursor depends on an existing anchor");
        });
        suite.AsyncTest("CatalogQueries_InvalidOptionsAndEmptyResults", async () =>
        {
            await using var host = await Host.Start();
            foreach (var route in new[] { "", "/feed" })
            {
                foreach (var query in new[] { "q=%00", "limited=normal", "limited=4", "sort=unknown", "direction=sideways", "q=" + new string('x', 101),
                    "pageSize=0", "pageSize=101" })
                    await host.Bad(route + "?" + query);
                using var empty = await host.Json(route + "?limited=limited&q=nonexistent");
                Check.That(Ids(empty).Length == 0 && !empty.RootElement.GetProperty("hasMore").GetBoolean(), "Empty search result contract changed");
                if (route == "/feed")
                    Check.That(empty.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null, "Empty feed produced cursor");
            }
            await host.Bad("/feed?sort=price&after=invalid");
            await host.Bad("/feed?sort=name&after=" + new string('a', 8000));
        });
    }

    private static readonly string[] Names = ["Zulu", "Alpha", "Alpha", "lower", "100%_special\\hat", "alpha", "Omega", "Missing"];
    private static readonly long?[] OriginalPrices = [150, 0, null, null, 75, null, null, null];
    private static readonly decimal?[] Prices = [100, 0, 100, null, 50, null, null, null];
    private static readonly decimal?[] Raps = [10.123456m, 0, 10.123456m, 2.2m, null, 50, null, null];
    private static readonly decimal?[] Sales = [2.123456m, 0, 2.123456m, 7, null, null, null, null];
    private static readonly ItemMarketStatus[] Statuses = [ItemMarketStatus.Limited, ItemMarketStatus.LimitedUnique, ItemMarketStatus.Limited,
        ItemMarketStatus.Normal, ItemMarketStatus.LimitedUnique, ItemMarketStatus.Collectible, ItemMarketStatus.Limited, ItemMarketStatus.LimitedUnique];
    private static DateTime? Created(int i) => i < 3 ? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        : i < 6 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
    private static long[] Expected(string sort, bool desc)
    {
        var rows = Enumerable.Range(0, 8);
        if (sort == "name")
            return (desc ? rows.OrderByDescending(i => Names[i], StringComparer.Ordinal) : rows.OrderBy(i => Names[i], StringComparer.Ordinal))
                .ThenBy(i => i).Select(i => (long)i + 1).ToArray();
        decimal? Key(int i) => sort switch { "price" => Prices[i], "rap" => Raps[i], "sales" => Sales[i], _ => Created(i)?.Ticks };
        var nonnull = rows.OrderBy(i => Key(i) is null);
        return (desc ? nonnull.ThenByDescending(Key) : nonnull.ThenBy(Key)).ThenBy(i => i).Select(i => (long)i + 1).ToArray();
    }
    private static long[] Ids(JsonDocument document) => document.RootElement.GetProperty("items").EnumerateArray()
        .Select(x => x.GetProperty("robloxAssetId").GetInt64()).ToArray();

    private static void AssertPrices(JsonElement item, decimal? original, decimal? current, decimal? rap, string route)
    {
        foreach (var (field, expected) in new[] { ("originalPriceRobux", original), ("currentPriceRobux", current), ("rapRobux", rap) })
        {
            Check.That(item.TryGetProperty(field, out var value), "Missing price field " + field + " in " + route);
            Check.That(expected.HasValue ? value.ValueKind == JsonValueKind.Number && value.GetDecimal() == expected.Value
                : value.ValueKind == JsonValueKind.Null, "Incorrect " + field + " for " + item.GetProperty("robloxAssetId") + " in " + route);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string schema;
        private readonly NpgsqlConnection admin;
        private readonly WebApplication app;
        private readonly HttpClient client;
        public LiroDbContext Db { get; }
        public List<string> Sql { get; }
        private Host(string schema, NpgsqlConnection admin, LiroDbContext db, WebApplication app, List<string> sql)
        {
            this.schema = schema; this.admin = admin; Db = db; this.app = app; Sql = sql;
            client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        }
        public static async Task<Host> Start()
        {
            var schema = "catalog_query_" + Guid.NewGuid().ToString("N");
            var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES")!;
            var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await create.ExecuteNonQueryAsync();
            var sql = new List<string>();
            var isolated = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
            var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(isolated).LogTo(sql.Add, LogLevel.Information).Options);
            await db.Database.OpenConnectionAsync();
            await using (var ddl = db.Database.GetDbConnection().CreateCommand())
            {
                ddl.CommandText = db.Database.GenerateCreateScript();
                await ddl.ExecuteNonQueryAsync();
            }
            for (var i = 0; i < 10; i++)
            {
                var item = new Item(i + 1, Names[i % 8], marketStatus: Statuses[i % 8]);
                db.Entry(item).Property(x => x.Id).CurrentValue = Guid.Parse("00000000-0000-0000-0000-" + (i + 1).ToString("D12"));
                item.UpdateDetails(new ItemCatalogDetails { CreatorId = i == 9 ? 2 : 1, CreatorType = "User",
                    AssetTypeId = i == 8 ? 28 : 8, CreatedAtUtc = Created(i) });
                if (i < 6)
                    item.ObserveMarket(new ItemMarketData { LowestResalePriceRobux = (long?)Prices[i], HasResellers = Prices[i].HasValue,
                        PriceBeforeDiscountRobux = OriginalPrices[i] }, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), "catalog-query-test");
                db.Items.Add(item);
                if (i < 6)
                    db.Set<MarketWatch>().Add(new MarketWatch { ItemId = item.Id, NextCheckAtUtc = DateTime.UtcNow, RecentAveragePrice = Raps[i], DailySales = Sales[i] });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(new ConfigurationBuilder().Build());
            builder.Services.RemoveAll<ItemService>();
            builder.Services.RemoveAll<ImportRobloxItemService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(CatalogController).Assembly);
            builder.Services.AddSingleton<ICatalogBrowseRepository>(new CatalogBrowseRepository(db));
            builder.Services.AddSingleton<IItemMarketSummaryRepository>(new ItemMarketSummaryRepository(db));
            var app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            return new Host(schema, admin, db, app, sql);
        }
        public async Task<JsonDocument> Json(string query)
        {
            using var response = await client.GetAsync("/api/v1/catalog" + query);
            var text = await response.Content.ReadAsStringAsync();
            Check.That(response.IsSuccessStatusCode, "Catalog request failed " + query + ": " + response.StatusCode + " " + text);
            return JsonDocument.Parse(text);
        }
        public async Task Bad(string query)
        {
            using var response = await client.GetAsync("/api/v1/catalog" + query);
            Check.That(response.StatusCode == HttpStatusCode.BadRequest, "Invalid query accepted: " + query[..Math.Min(120, query.Length)]);
        }
        public async Task<long[]> Traverse(string query, int size)
        {
            var seen = new List<long>();
            string? cursor = null;
            do
            {
                using var page = await Json("/feed?" + query + "&pageSize=" + size + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)));
                seen.AddRange(Ids(page));
                cursor = page.RootElement.GetProperty("nextCursor").GetString();
                Check.That(page.RootElement.GetProperty("hasMore").GetBoolean() == (cursor is not null), "hasMore disagrees with nextCursor");
                Check.That(seen.Count <= 8 && seen.Distinct().Count() == seen.Count, "Unbounded or duplicate feed traversal");
            } while (cursor is not null);
            return seen.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await Db.DisposeAsync();
            await using (var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin)) await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }
}
