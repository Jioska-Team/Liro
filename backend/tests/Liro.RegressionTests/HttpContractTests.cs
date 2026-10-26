using System.Net;
using System.Text;
using System.Text.Json;
using Liro.Api;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Liro.RegressionTests;

internal static class HttpContractTests
{
    public static void Register(RegressionSuite suite)
    {
        RegisterWire(suite);
        suite.AsyncTest("HttpContracts_OpenApiDescribesEveryControllerSuccess", async () =>
        {
            await using var host = await Host.Start();
            using var document = await host.Json("/openapi/v1.json");
            var paths = document.RootElement.GetProperty("paths");
            foreach (var path in new[] { "/api/v1/catalog", "/api/v1/catalog/feed" })
            {
                var parameters = paths.GetProperty(path).GetProperty("get").GetProperty("parameters").EnumerateArray().ToArray();
                foreach (var name in new[] { "limited", "q", "sort", "direction" })
                    Check.That(parameters.Any(x => x.GetProperty("name").GetString() == name
                        && x.GetProperty("in").GetString() == "query"
                        && (!x.TryGetProperty("required", out var required) || !required.GetBoolean())),
                        path + " omits optional query parameter " + name);
            }
            var expectations = new (string Path, string Method, string Status, string Schema, bool Array)[]
            {
                ("/api/v1/catalog", "get", "200", "CatalogPageResponse", false),
                ("/api/v1/catalog/feed", "get", "200", "CatalogFeedResponse", false),
                ("/api/v1/catalog/bundles/{bundleId}/components", "get", "200", "BundleComponentsResponse", false),
                ("/api/v1/catalog/assets/{assetId}/bundles", "get", "200", "ItemResponse", true),
                ("/api/v1/catalog/imports", "post", "200", "ItemResponse", false),
                ("/api/infrastructure", "get", "200", "InfrastructureResponse", false),
                ("/api/v1/items/{id}/market-history", "get", "200", "ItemMarketResponse", true),
                ("/api/v1/items/{id}/market-history/daily-sales", "get", "200", "DailySalesPage", false),
                ("/api/v1/items/{id}/market-history/rap", "get", "200", "RapHistoryPage", false),
                ("/api/v1/items/{id}/market-summary", "get", "200", "ItemMarketSummary", false),
                ("/api/v1/items/{id}", "get", "200", "ItemResponse", false),
                ("/api/v1/items/by-asset/{assetId}", "get", "200", "ItemResponse", false),
                ("/api/v1/items/by-bundle/{bundleId}", "get", "200", "ItemResponse", false),
                ("/api/v1/items/import-bundle/{bundleId}", "post", "200", "ItemResponse", false),
                ("/api/v1/items/import/{assetId}", "post", "200", "ItemResponse", false),
                ("/api/v1/items", "post", "201", "ItemResponse", false)
            };
            foreach (var expected in expectations)
            {
                var operation = paths.GetProperty(expected.Path).GetProperty(expected.Method);
                var responses = operation.GetProperty("responses");
                var schema = responses.GetProperty(expected.Status).GetProperty("content").GetProperty("application/json").GetProperty("schema");
                if (expected.Array)
                {
                    Check.That(schema.GetProperty("type").GetString() == "array", expected.Path + " lost its array response");
                    schema = schema.GetProperty("items");
                }
                Check.That(schema.GetProperty("$ref").GetString() == "#/components/schemas/" + expected.Schema,
                    expected.Path + " has missing or incorrect success schema");
                if (expected.Path.StartsWith("/api/v1/", StringComparison.Ordinal))
                {
                    Check.That(responses.TryGetProperty("429", out _), expected.Path + " omits rate-limit response");
                    var throttled = responses.GetProperty("429");
                    Check.That(throttled.GetProperty("headers").GetProperty("Retry-After").TryGetProperty("schema", out _)
                        && throttled.GetProperty("content").TryGetProperty("application/problem+json", out _), expected.Path + " omits rate-limit delay or problem media type");
                }
                if (expected.Method == "post")
                {
                    Check.That(responses.TryGetProperty("401", out var challenge) && (!challenge.TryGetProperty("content", out var content) || !content.EnumerateObject().Any()), expected.Path + " must document an empty administrative challenge");
                    Check.That(operation.GetProperty("security").EnumerateArray().Any(x => x.TryGetProperty("AdminKey", out _)),
                        expected.Path + " omits administrative security requirement");
                }
                else
                    Check.That(!operation.TryGetProperty("security", out var security) || security.GetArrayLength() == 0,
                        expected.Path + " incorrectly requires credentials for a public read");
            }
            var scheme = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("AdminKey");
            Check.That(scheme.GetProperty("type").GetString() == "apiKey" && scheme.GetProperty("in").GetString() == "header"
                && scheme.GetProperty("name").GetString() == "X-Liro-Admin-Key", "OpenAPI administrative credential header differs from authentication");
            foreach (var expected in expectations)
            {
                var responses = paths.GetProperty(expected.Path).GetProperty(expected.Method).GetProperty("responses");
                var codes = new List<string> { "500", "503" };
                if (expected.Path.StartsWith("/api/v1/", StringComparison.Ordinal)) codes.Add("400");
                if (expected.Path != "/api/v1/catalog" && expected.Path != "/api/v1/catalog/feed"
                    && expected.Path != "/api/v1/catalog/assets/{assetId}/bundles" && expected.Path != "/api/v1/items") codes.Add("404");
                if (expected.Method == "post") codes.Add("409");
                if (expected.Path.Contains("import", StringComparison.Ordinal)) codes.Add("502");
                if (expected.Path is "/api/v1/catalog/imports" or "/api/v1/items") codes.Add("415");
                foreach (var code in codes)
                {
                    var expectedSchema = expected.Path == "/api/infrastructure" && code == "503" ? "InfrastructureResponse" : "ProblemDetails";
                    var response = responses.GetProperty(code);
                    var media = code is "400" or "404" or "409" or "415" ? "application/problem+json" : "application/json";
                    var schema = response.GetProperty("content").GetProperty(media).GetProperty("schema");
                    if (code == "400")
                    {
                        var alternatives = schema.GetProperty("anyOf").EnumerateArray().ToArray();
                        Check.That(alternatives.Length == 2 && alternatives[0].GetProperty("$ref").GetString() == "#/components/schemas/ProblemDetails", expected.Path + " lacks business validation errors");
                        var validation = alternatives[1];
                        if (validation.TryGetProperty("$ref", out var reference))
                            validation = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
                        var details = validation.GetProperty("properties").GetProperty("errors").GetProperty("additionalProperties");
                        Check.That(details.GetProperty("type").GetString() == "array" && details.GetProperty("items").GetProperty("type").GetString() == "string", expected.Path + " lacks field validation error arrays");
                    }
                    else Check.That(schema.GetProperty("$ref").GetString() == "#/components/schemas/" + expectedSchema, expected.Path + " has incorrect error schema " + code);
                }
            }
            Check.That(paths.GetProperty("/api/v1/items").GetProperty("post").GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("Location", out _), "Creation omits Location");
            Check.That(paths.GetProperty("/api/v1/catalog/imports").GetProperty("post").GetProperty("responses").GetProperty("503").GetProperty("headers").TryGetProperty("Retry-After", out _), "Import cooldown omits optional Retry-After");
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            var errors = paths.GetProperty("/api/v1/catalog").GetProperty("get").GetProperty("responses");
            Check.That(errors.GetProperty("400").GetProperty("content").TryGetProperty("application/problem+json", out _), "MVC errors must document application/problem+json");
            Check.That(errors.GetProperty("500").GetProperty("content").TryGetProperty("application/json", out _), "Exception handler must retain application/json");
            ExactProperties(schemas.GetProperty("CatalogPageResponse").GetProperty("properties"), "items", "page", "pageSize", "hasMore");
            ExactProperties(schemas.GetProperty("BundleComponentsResponse").GetProperty("properties"), "bundle", "components", "pendingAssetIds", "relationshipsObservedAtUtc");
            ExactProperties(schemas.GetProperty("InfrastructureResponse").GetProperty("properties"), "postgres", "valkey", "valkeyLatencyMs");
        });
    }

    public static void RegisterWire(RegressionSuite suite)
    {
        suite.AsyncTest("HttpContracts_CatalogPagesPreserveFieldsNullsAndCursor", async () =>
        {
            await using var host = await Host.Start();
            using var populated = await host.Json("/api/v1/catalog?page=2&pageSize=3");
            var page = populated.RootElement;
            ExactProperties(page, "items", "page", "pageSize", "hasMore");
            Check.That(page.GetProperty("page").GetInt32() == 2 && page.GetProperty("pageSize").GetInt32() == 3
                && page.GetProperty("hasMore").GetBoolean() && page.GetProperty("items").GetArrayLength() == 1, "Legacy pagination response changed");
            AssertItem(page.GetProperty("items")[0], host.Item);
            using var feed = await host.Json("/api/v1/catalog/feed?pageSize=3");
            ExactProperties(feed.RootElement, "items", "pageSize", "hasMore", "nextCursor");
            var cursor = feed.RootElement.GetProperty("nextCursor").GetString();
            Check.That(Liro.Api.Contracts.CatalogCursorCodec.TryDecode(cursor, out var position) && position?.Id == host.Item.Id,
                "Feed does not preserve the item position");
            host.Browse.Empty = true;
            using var empty = await host.Json("/api/v1/catalog");
            Check.That(empty.RootElement.GetProperty("items").GetArrayLength() == 0 && !empty.RootElement.GetProperty("hasMore").GetBoolean(), "Empty catalog contract changed");
            using var end = await host.Json("/api/v1/catalog/feed?after=" + Uri.EscapeDataString(cursor!));
            Check.That(end.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                && !end.RootElement.GetProperty("hasMore").GetBoolean(), "Feed terminal null cursor changed");
        });
        suite.AsyncTest("HttpContracts_ComponentsAndParentsPreserveWireShape", async () =>
        {
            await using var host = await Host.Start();
            using var result = await host.Json("/api/v1/catalog/bundles/2/components");
            var root = result.RootElement;
            ExactProperties(root, "bundle", "components", "pendingAssetIds", "relationshipsObservedAtUtc");
            Check.That(root.GetProperty("bundle").GetProperty("itemType").GetString() == "Bundle"
                && root.GetProperty("bundle").GetProperty("robloxAssetId").ValueKind == JsonValueKind.Null
                && root.GetProperty("components").GetArrayLength() == 1
                && root.GetProperty("pendingAssetIds").EnumerateArray().Select(x => x.GetInt64()).SequenceEqual(new long[] { 7, 9 })
                && root.GetProperty("relationshipsObservedAtUtc").ValueKind == JsonValueKind.Null, "Bundle contents contract changed");
            using var parents = await host.Json("/api/v1/catalog/assets/1/bundles");
            Check.That(parents.RootElement.ValueKind == JsonValueKind.Array && parents.RootElement.GetArrayLength() == 1
                && parents.RootElement[0].GetProperty("robloxItemId").GetInt64() == 2, "Parents must remain an item array");
            host.Browse.Empty = true;
            using var emptyParents = await host.Json("/api/v1/catalog/assets/1/bundles");
            Check.That(emptyParents.RootElement.GetArrayLength() == 0, "Empty parents should remain 200 with []");
        });
        suite.AsyncTest("HttpContracts_ItemsAndMarketPreserveArraysNumbersAndMissingValues", async () =>
        {
            await using var host = await Host.Start();
            using var item = await host.Json("/api/v1/items/by-asset/1");
            AssertItem(item.RootElement, host.Item);
            var path = $"/api/v1/items/{host.Item.Id}";
            using var legacy = await host.Json(path + "/market-history?limit=1&before=2026-10-01T00:00:00Z");
            Check.That(legacy.RootElement.ValueKind == JsonValueKind.Array && legacy.RootElement.GetArrayLength() == 0, "Legacy history must remain an array");
            using var daily = await host.Json(path + "/market-history/daily-sales?from=2026-10-01&to=2026-10-03&limit=1");
            ExactProperties(daily.RootElement, "fromUtc", "toUtcExclusive", "points", "nextAfter");
            Check.That(daily.RootElement.GetProperty("fromUtc").GetString() == "2026-10-01"
                && daily.RootElement.GetProperty("toUtcExclusive").GetString() == "2026-10-03"
                && daily.RootElement.GetProperty("points")[0].GetProperty("averagePriceRobux").ValueKind == JsonValueKind.Null
                && daily.RootElement.GetProperty("points")[0].GetProperty("salesVolume").GetDecimal() == 0
                && daily.RootElement.GetProperty("nextAfter").GetString() == "2026-10-01", "Daily missing values, zero or cursor changed");
            using var rap = await host.Json(path + "/market-history/rap?from=2026-10-01&to=2026-10-03");
            Check.That(rap.RootElement.GetProperty("points")[0].GetProperty("rap").GetDecimal() == 12.5m
                && rap.RootElement.GetProperty("nextAfter").ValueKind == JsonValueKind.Null, "RAP decimal or null cursor changed");
            using var summary = await host.Json(path + "/market-summary");
            Check.That(summary.RootElement.GetProperty("itemId").GetGuid() == host.Item.Id
                && summary.RootElement.GetProperty("dailySales").GetDecimal() == 0
                && summary.RootElement.GetProperty("recentAveragePrice").ValueKind == JsonValueKind.Null, "Market summary missing and zero values changed");
        });
        suite.AsyncTest("HttpContracts_InvalidMissingAndUnauthorizedKeepStatusAndProblemBodies", async () =>
        {
            await using var host = await Host.Start();
            foreach (var path in new[] { "/api/v1/catalog?page=0", "/api/v1/catalog/feed?after=", "/api/v1/catalog/bundles/0/components",
                "/api/v1/items/by-asset/0", $"/api/v1/items/{host.Item.Id}/market-history?limit=201",
                $"/api/v1/items/{host.Item.Id}/market-history/rap?from=invalid" })
            {
                using var response = await host.Client.GetAsync(path);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check.That(response.StatusCode == HttpStatusCode.BadRequest && problem.RootElement.GetProperty("status").GetInt32() == 400,
                    path + " changed its 400 problem contract");
                Check.That(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "MVC error content type changed");
                if (path.EndsWith("from=invalid", StringComparison.Ordinal))
                    Check.That(problem.RootElement.GetProperty("errors").GetProperty("from").GetArrayLength() > 0, "Binding errors lost their details");
            }
            foreach (var path in new[] { "/api/v1/items/by-asset/999", "/api/v1/catalog/bundles/999/components", $"/api/v1/items/{Guid.Empty}/market-summary" })
            {
                using var response = await host.Client.GetAsync(path);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check.That(response.StatusCode == HttpStatusCode.NotFound && problem.RootElement.GetProperty("status").GetInt32() == 404,
                    path + " changed its 404 problem contract");
            }
            using var denied = await host.Client.PostAsync("/api/v1/catalog/imports", new StringContent("{}", Encoding.UTF8, "application/json"));
            Check.That(denied.StatusCode == HttpStatusCode.Unauthorized && (await denied.Content.ReadAsByteArrayAsync()).Length == 0,
                "Administrative challenge must remain an empty 401 before validation");
        });
    }

    private static void ExactProperties(JsonElement value, params string[] names) =>
        Check.That(value.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(names.Order()), "JSON properties changed: " + value.GetRawText());

    private static void AssertItem(JsonElement value, Item item)
    {
        Check.That(value.GetProperty("id").GetGuid() == item.Id && value.GetProperty("robloxItemId").GetInt64() == 1
            && value.GetProperty("marketStatus").ValueKind == JsonValueKind.Number
            && value.GetProperty("marketStatus").GetInt32() == (int)ItemMarketStatus.Normal
            && value.GetProperty("market").ValueKind == JsonValueKind.Null
            && value.GetProperty("originalPriceRobux").ValueKind == JsonValueKind.Null
            && value.GetProperty("currentPriceRobux").ValueKind == JsonValueKind.Null
            && value.GetProperty("rapRobux").ValueKind == JsonValueKind.Null
            && value.GetProperty("thumbnailUrl").ValueKind == JsonValueKind.Null
            && !value.TryGetProperty("domainEvents", out _), "Item identity, numeric enum, nulls or internal state changed");
    }

    private sealed class Host(WebApplication app, HttpClient client, Item item, BrowseRepository browse) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public Item Item => item;
        public BrowseRepository Browse => browse;
        public async Task<JsonDocument> Json(string path) => JsonDocument.Parse(await client.GetStringAsync(path));
        public static async Task<Host> Start()
        {
            var item = Fixtures.Item();
            var repository = new MemoryRepository(item);
            var browse = new BrowseRepository(item);
            var market = new MarketRepository(item.Id);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            builder.Services.RemoveAll<ItemService>();
            builder.Services.RemoveAll<ImportRobloxItemService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(CatalogController).Assembly);
            builder.Services.AddSingleton(new ItemService(repository));
            builder.Services.AddSingleton(Fixtures.Importer(repository));
            builder.Services.AddSingleton<ICatalogBrowseRepository>(browse);
            builder.Services.AddSingleton<IItemMarketHistoryRepository>(market);
            builder.Services.AddSingleton<IItemMarketSeriesRepository>(market);
            builder.Services.AddSingleton<IItemMarketSummaryRepository>(market);
            var app = builder.Build();
            app.UseExceptionHandler();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapControllers();
            app.MapOpenApi();
            await app.StartAsync();
            return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) }, item, browse);
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class BrowseRepository(Item item) : ICatalogBrowseRepository
    {
        private readonly Item bundle = new(2, "Bundle", itemType: CatalogItemType.Bundle);
        public bool Empty { get; set; }
        public Task<CatalogPage> BrowseAsync(int page, int pageSize, CancellationToken ct) => Task.FromResult(new CatalogPage(Empty ? [] : [item], !Empty));
        public Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CancellationToken ct) => BrowseAsync(1, pageSize, ct);
        public Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct) => Task.FromResult<BundleContents?>(bundleId == 2 ? new(bundle, [item], [7, 9]) : null);
        public Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Item>>(Empty ? [] : [bundle]);
        public Task<IReadOnlyList<CatalogItemKey>> GetBundlesDueAsync(DateTime before, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetMissingComponentsAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task SynchronizeRelationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetBundlesMissingThumbnailAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveBundleThumbnailAsync(long bundleId, string? url, DateTime checkedAt, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MarketRepository(Guid id) : IItemMarketHistoryRepository, IItemMarketSeriesRepository, IItemMarketSummaryRepository
    {
        public Task<IReadOnlyDictionary<Guid, decimal?>> GetRapAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, decimal?>>(new Dictionary<Guid, decimal?>());
        private static readonly DateTime Observed = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        public Task<IReadOnlyList<ItemMarketSnapshot>> GetBeforeAsync(Guid itemId, DateTime beforeUtc, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMarketSnapshot>>([]);
        public Task<DailySalesPage> GetDailyAsync(Guid itemId, DateOnly from, DateOnly to, DateOnly? after, int limit, CancellationToken ct) => Task.FromResult(new DailySalesPage(from, to, [new(from, null, 0, Observed)], from));
        public Task<RapHistoryPage> GetRapAsync(Guid itemId, DateOnly from, DateOnly to, DateTimeOffset? after, int limit, CancellationToken ct) => Task.FromResult(new RapHistoryPage(from, to, [new(Observed, 12.5m)], null));
        public Task<ItemMarketSummary?> GetAsync(Guid itemId, CancellationToken ct) => Task.FromResult<ItemMarketSummary?>(itemId == id ? new() { ItemId = id, RobloxItemId = 1, ItemType = "Asset", DailySales = 0 } : null);
    }
}
