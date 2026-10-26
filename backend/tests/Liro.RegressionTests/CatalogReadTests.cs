using System.Net;
using System.Text.Json;
using Liro.Api;
using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Liro.RegressionTests;

internal static class CatalogReadTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("CatalogCursor_RejectsWrongScopeAndInvalidPositions", () =>
        {
            string Token(string value) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(value));
            var id = Guid.NewGuid();
            foreach (var value in new[] { $"other-v1|null|{id:D}", $"catalog-v2|null|{id:D}", $"catalog-v1|null|{Guid.Empty:D}",
                $"catalog-v1|2026-01-01T00:00:00.0000000|{id:D}", $"catalog-v1|invalid|{id:D}", $"catalog-v1|null|{id:D}|extra" })
                Check.That(!Liro.Api.Contracts.CatalogCursorCodec.TryDecode(Token(value), out _), "Malformed or foreign cursor was accepted");
            foreach (DateTime? date in new DateTime?[] { null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560) })
            {
                var expected = new CatalogCursor(date, id);
                Check.That(Liro.Api.Contracts.CatalogCursorCodec.TryDecode(Liro.Api.Contracts.CatalogCursorCodec.Encode(expected), out var actual)
                    && actual == expected, "Cursor loses timestamp precision or identity");
            }
        });
        suite.AsyncTest("CatalogReads_ConcurrencyAndHealthIsolation", async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(new ConfigurationBuilder().Build());
            await using var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            var entered = 0;
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.MapGet("/api/v1/catalog/blocked", async () =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    both.TrySetResult();
                await release.Task;
                return Results.Ok();
            });
            app.MapGet("/api/lan/ready", () => Results.Ok());
            app.MapPost("/api/v1/catalog/imports", () => Results.Ok());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
            var first = client.GetAsync("/api/v1/catalog/blocked");
            var second = client.GetAsync("/api/v1/catalog/blocked");
            try
            {
                await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var rejected = await client.GetAsync("/api/v1/catalog/blocked");
                Check.That(rejected.StatusCode == HttpStatusCode.TooManyRequests && rejected.Headers.RetryAfter is not null,
                    "Concurrent catalog reads were not rejected with Retry-After");
                using var health = await client.GetAsync("/api/lan/ready");
                using var write = await client.PostAsync("/api/v1/catalog/imports", null);
                Check.That(health.IsSuccessStatusCode && write.IsSuccessStatusCode && entered == 2, "Read limit blocked health/writes or reached storage");
            }
            finally { release.TrySetResult(); await Task.WhenAll(first, second); await app.StopAsync(); }
        });

        suite.AsyncTest("CatalogReads_ClientAndGlobalFrequency", async () =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLiroApi(new ConfigurationBuilder().Build());
            using var provider = services.BuildServiceProvider();
            var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
            Check.That(limiter is not null, "Public reads have no global protection");
            DefaultHttpContext Request(string ip, string path = "/api/v1/items/by-asset/1")
            {
                var context = new DefaultHttpContext();
                context.Request.Method = "GET";
                context.Request.Path = path;
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                context.Request.Headers["X-Forwarded-For"] = Guid.NewGuid().ToString();
                return context;
            }
            for (var i = 0; i < 60; i++)
            {
                using var lease = await limiter!.AcquireAsync(Request("192.0.2.1"));
                Check.That(lease.IsAcquired, "Client quota rejected too early");
            }
            using (var denied = await limiter!.AcquireAsync(Request("192.0.2.1")))
                Check.That(!denied.IsAcquired, "Client quota can be bypassed by a forwarded header");
            // A denied request may conservatively consume a global token, so at most 60 remain.
            var admitted = 0;
            for (var i = 0; i < 60; i++)
            {
                using var lease = await limiter!.AcquireAsync(Request("192.0.2.2"));
                if (lease.IsAcquired)
                    admitted++;
            }
            Check.That(admitted is >= 59 and <= 60, "Client partitions are not independent");
            using (var denied = await limiter!.AcquireAsync(Request("192.0.2.3")))
                Check.That(!denied.IsAcquired, "API-wide frequency limit can be bypassed with another client");
            foreach (var path in new[] { "/api/lan/ready", "/health", "/alive", "/api/v1/catalogue" })
            {
                using var health = await limiter!.AcquireAsync(Request("192.0.2.1", path));
                Check.That(health.IsAcquired, "Non-catalog routes share read quota");
            }
        });

        suite.AsyncTest("CatalogCursor_PostgresTraversalAndLegacyContract", async () =>
        {
            var schema = "catalog_cursor_" + Guid.NewGuid().ToString("N");
            var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES")!;
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
                await create.ExecuteNonQueryAsync();
            try
            {
                var sql = new List<string>();
                var isolated = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
                await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(isolated)
                    .LogTo(sql.Add, LogLevel.Information).Options);
                await db.Database.OpenConnectionAsync();
                await using (var ddl = db.Database.GetDbConnection().CreateCommand())
                {
                    ddl.CommandText = db.Database.GenerateCreateScript();
                    await ddl.ExecuteNonQueryAsync();
                }
                for (var i = 1; i <= 9; i++)
                {
                    var item = new Item(i, "Cursor item " + i);
                    item.UpdateDetails(new ItemCatalogDetails
                    {
                        CreatorId = i == 9 ? 2 : 1,
                        CreatorType = "User",
                        AssetTypeId = i == 8 ? 28 : 8,
                        CreatedAtUtc = i <= 4 ? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) : i == 5 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null
                    });
                    db.Items.Add(item);
                }
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                var repository = new CatalogBrowseRepository(db);
                var expected = (await repository.BrowseAsync(1, 100, default)).Items.Select(x => x.Id).ToArray();
                Check.That(expected.Length == 7, "Fixture visibility is incorrect");
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddLiroApi(new ConfigurationBuilder().Build());
                builder.Services.AddControllers().AddApplicationPart(typeof(Liro.Api.Controllers.CatalogController).Assembly);
                builder.Services.AddSingleton<ICatalogBrowseRepository>(repository);
                builder.Services.AddSingleton<IItemMarketSummaryRepository>(new ItemMarketSummaryRepository(db));
                await using var app = builder.Build();
                app.UseRouting();
                app.UseRateLimiter();
                app.MapControllers();
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                try
                {
                    using var legacy = JsonDocument.Parse(await client.GetStringAsync("/api/v1/catalog?page=2&pageSize=2"));
                    Check.That(legacy.RootElement.GetProperty("page").GetInt32() == 2 && legacy.RootElement.GetProperty("pageSize").GetInt32() == 2
                        && legacy.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).SequenceEqual(expected.Skip(2).Take(2)), "Legacy pagination contract changed");
                    var seen = new List<Guid>();
                    string? after = null;
                    string? firstCursor = null;
                    sql.Clear();
                    do
                    {
                        using var response = await client.GetAsync("/api/v1/catalog/feed?pageSize=2" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));
                        Check.That(response.IsSuccessStatusCode, "Cursor endpoint unavailable");
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        seen.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
                        after = json.RootElement.GetProperty("nextCursor").GetString();
                        firstCursor ??= after;
                        Check.That(json.RootElement.GetProperty("hasMore").GetBoolean() == (after is not null), "Cursor termination is inconsistent");
                        Check.That(seen.Count <= expected.Length, "Cursor traversal repeats rows");
                    } while (after is not null);
                    Check.That(seen.SequenceEqual(expected) && seen.Distinct().Count() == 7, "Cursor skips or repeats tied dates or null dates");
                    Check.That(!sql.Any(x => x.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)), "Cursor still uses OFFSET");
                    // Deleting the anchor must not invalidate the position encoded in the cursor.
                    await db.Items.Where(x => x.Id == expected[1]).ExecuteDeleteAsync();
                    using var resumed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/catalog/feed?pageSize=2&after=" + firstCursor));
                    Check.That(resumed.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid() == expected[2], "Deleted anchor prevents continuation");
                    foreach (var query in new[] { "pageSize=0", "pageSize=101", "after=invalid", "after=", "after=" + new string('a', 600), "page=2", "after=YWJj" })
                    {
                        using var invalid = await client.GetAsync("/api/v1/catalog/feed?" + query);
                        Check.That(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid cursor input was accepted: " + query[..Math.Min(query.Length, 30)]);
                    }
                    await db.Items.ExecuteDeleteAsync();
                    using var empty = JsonDocument.Parse(await client.GetStringAsync("/api/v1/catalog/feed"));
                    Check.That(empty.RootElement.GetProperty("items").GetArrayLength() == 0 && empty.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null, "Empty cursor response incorrect");
                }
                finally { await app.StopAsync(); }
            }
            finally { await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await drop.ExecuteNonQueryAsync(); }
        });
    }
}
