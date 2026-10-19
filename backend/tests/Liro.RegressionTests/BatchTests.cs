using System.Net;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.CatalogWorker.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Infrastructure.Roblox;
namespace Liro.RegressionTests;

internal static class BatchTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Batch worker uses three detail requests for 25 new items and skips known items", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                var keys = ReadKeys(request);
                Check.That(keys.Length == (calls < 3 ? 10 : 5) && keys.All(x => x.Id != 1), "Known item or oversized batch sent");
                return Details(keys);
            });
            var repository = new MemoryRepository(Fixtures.Item());
            using var provider = Provider(repository, new MemoryFailureStore(), new RobloxCatalogClient(http));
            var summary = await CatalogBatchProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), Enumerable.Range(1, 26).Select(x => new CatalogItemKey(x)), true,
                new CatalogWorkerOptions { DetailsBatchSize = 10, ItemDelay = TimeSpan.Zero }, default);
            Check.That(summary.Added == 25 && summary.Failed == 0 && calls == 3 && repository.SavedAssetIds.Order().SequenceEqual(Enumerable.Range(2, 25).Select(x => (long)x)), "Batching lost records or sent per-item details requests");
        });
        suite.AsyncTest("Batch worker retries only partial failures and refresh still updates existing items", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                return calls == 1 ? Json("""{"data":[{"id":2,"itemType":"Asset","name":"Healthy","itemRestrictions":[],"creatorTargetId":1,"creatorType":"User"},{"id":4,"itemType":"Asset","name":"Bad"}]}""") : Details(ReadKeys(request));
            });
            var failures = new MemoryFailureStore();
            var repository = new MemoryRepository(Fixtures.Item());
            using var provider = Provider(repository, failures, new RobloxCatalogClient(http));
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var options = new CatalogWorkerOptions { ItemDelay = TimeSpan.Zero };
            await CatalogBatchProcessor.ProcessAsync(scopes, new CatalogItemKey[] { new(2), new(3), new(4) }, true, options, default);
            Check.That(repository.SavedAssetIds.SequenceEqual(new long[] { 2 }), "Partial response saved invalid items");
            Check.That((await failures.GetDueAsync(DateTime.UtcNow, 10, default)).Order().SequenceEqual(new long[] { 3, 4 }), "Healthy item queued or failed item lost");
            await CatalogBatchProcessor.ProcessAsync(scopes, new CatalogItemKey[] { new(1) }, false, options, default);
            Check.That(calls == 2 && repository.SavedAssetIds.SequenceEqual(new long[] { 2, 1 }), "Refresh did not update existing item");
        });
        suite.AsyncTest("Batch invalid count splits requests while preserving results", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                var keys = ReadKeys(request);
                return keys.Length > 2 ? new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"errors":[{"code":2,"message":"Invalid count"}]}""")
                } : Details(keys);
            });
            var results = await new RobloxCatalogClient(http).GetItemsAsync(Enumerable.Range(1, 5).Select(x => new CatalogItemKey(x)).ToArray());
            Check.That(calls == 5 && results.Count == 5 && results.Values.All(x => x.Item is not null && x.Error is null), "Adaptive split lost data or failed to reduce count");
        });
        suite.AsyncTest("Batch CSRF challenge retries the whole payload once", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                Check.That(ReadKeys(request).Length == 2, "CSRF retry changed batch");
                if (calls == 1)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    challenge.Headers.Add("x-csrf-token", "test-challenge");
                    return challenge;
                }
                Check.That(request.Headers.GetValues("x-csrf-token").Single() == "test-challenge", "Challenge missing");
                return Details(ReadKeys(request));
            });
            var results = await new RobloxCatalogClient(http).GetItemsAsync([new(1), new(2)]);
            Check.That(calls == 2 && results.Values.All(x => x.Item is not null), "CSRF challenge caused per-item retries");
        });
        suite.AsyncTest("Batch rate limit preserves retry budgets and stops later requests", async () =>
        {
            var calls = 0;
            using var http = Http(_ => { calls++; return new(HttpStatusCode.TooManyRequests); });
            var failures = new MemoryFailureStore();
            using var provider = Provider(new MemoryRepository(Fixtures.Item()), failures, new RobloxCatalogClient(http));
            var error = await Check.ThrowsAsync<HttpRequestException>(() => CatalogBatchProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(),
                new CatalogItemKey[] { new(2), new(3), new(4) }, true, new CatalogWorkerOptions { DetailsBatchSize = 2, ItemDelay = TimeSpan.Zero }, default));
            Check.That(error.StatusCode == HttpStatusCode.TooManyRequests && calls == 1 && (await failures.GetDueAsync(DateTime.UtcNow, 10, default)).Count == 0, "429 consumed item retries or sent more batches");
        });
        suite.AsyncTest("Batch duplicate results fail only that identity", async () =>
        {
            using var http = Http(_ => Json("""{"data":[{"id":1,"itemType":"Asset","name":"One","itemRestrictions":[]},{"id":1,"itemType":"Asset","name":"Duplicate","itemRestrictions":[]},{"id":2,"itemType":"Asset","name":"Two","itemRestrictions":[]}]}"""));
            var results = await new RobloxCatalogClient(http).GetItemsAsync([new(1), new(2)]);
            Check.That(results[new(1)].Error is not null && results[new(1)].Item is null && results[new(2)].Item?.Name == "Two", "Duplicate identity was accepted or corrupted another item");
        });
        suite.AsyncTest("Batch details correlate unordered asset and bundle results with one request", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new StubHttp(request =>
            {
                calls++;
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Check.That(body.RootElement.GetProperty("items").GetArrayLength() == 2, "Details were not batched");
                return Json("""{"data":[{"id":126,"itemType":"Bundle","name":"Bundle","itemRestrictions":[]},{"id":126,"itemType":"Asset","name":"Asset","itemRestrictions":[]}]}""");
            }))
            {
                BaseAddress = new("https://catalog.roblox.com/")
            };
            var asset = new CatalogItemKey(126);
            var bundle = new CatalogItemKey(126, CatalogItemType.Bundle);
            var results = await new RobloxCatalogClient(http).GetItemsAsync([asset, bundle]);
            Check.That(calls == 1 && results[asset].Item?.Name == "Asset" && results[bundle].Item?.Name == "Bundle", "Batch results crossed typed identities");
        });
        suite.AsyncTest("Batch details preserve healthy items and identify missing and malformed results", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => Json("""{"data":[{"id":1,"itemType":"Asset","name":"Healthy","itemRestrictions":[]},{"id":2,"itemType":"Asset","name":"Bad"}]}"""))) { BaseAddress = new("https://catalog.roblox.com/") };
            var result = await new RobloxCatalogClient(http).GetItemsAsync([new(1), new(2), new(3)]);
            Check.That(result[new(1)].Item?.Name == "Healthy" && result[new(1)].Error is null, "Healthy item lost");
            Check.That(result[new(2)].Error is not null && result[new(3)].Error is not null, "Partial failures were treated as success");
        });
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new StubHttp(response)) { BaseAddress = new("https://catalog.roblox.com/") };
    private static CatalogItemKey[] ReadKeys(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return body.RootElement.GetProperty("items").EnumerateArray().Select(x => new CatalogItemKey(x.GetProperty("id").GetInt64(), (CatalogItemType)x.GetProperty("itemType").GetInt32())).ToArray();
    }
    private static HttpResponseMessage Details(CatalogItemKey[] keys) => Json(JsonSerializer.Serialize(new { data = keys.Select(x => new { id = x.Id, itemType = x.Type.ToString(), name = "Item " + x.Id, itemRestrictions = Array.Empty<string>(), creatorTargetId = 1, creatorType = "User" }) }));
    private static ServiceProvider Provider(MemoryRepository repository, MemoryFailureStore failures, IRobloxCatalogClient catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IItemRepository>(repository);
        services.AddSingleton<ICatalogImportFailureStore>(failures);
        services.AddSingleton(catalog);
        services.AddSingleton<IRobloxThumbnailClient>(new PendingThumbnail());
        services.AddSingleton<ILogger<ImportRobloxItemService>>(NullLogger<ImportRobloxItemService>.Instance);
        services.AddSingleton<ILogger<CatalogImportProcessor>>(NullLogger<CatalogImportProcessor>.Instance);
        services.AddSingleton(new CatalogRefreshPolicy());
        services.AddScoped<ImportRobloxItemService>();
        services.AddScoped<CatalogImportProcessor>();
        return services.BuildServiceProvider();
    }
}



