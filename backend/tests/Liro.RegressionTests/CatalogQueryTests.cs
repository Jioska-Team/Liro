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
                    item.ObserveMarket(new ItemMarketData { LowestResalePriceRobux = (long?)Prices[i] }, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), "catalog-query-test");
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
