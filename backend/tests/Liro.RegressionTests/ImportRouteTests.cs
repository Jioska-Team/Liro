using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Liro.Api;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class ImportRouteTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("ImportRoutes_CanonicalAndLegacyShareIdentityAndBudget", async () =>
        {
            await using var host = await Host.Start();
            foreach (var type in new[] { "Asset", "Bundle" })
            {
                using var first = await host.Client.PostAsJsonAsync("/api/v1/catalog/imports", new
                {
                    itemType = type,
                    robloxId = 126
                });
                Check.That(first.StatusCode == HttpStatusCode.OK, $"Canonical {type} import returned {first.StatusCode}");
                using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
                var legacy = type == "Asset" ? "/api/v1/items/import/126" : "/api/v1/items/import-bundle/126";
                using var old = await host.Client.PostAsync(legacy, null);
                using var oldJson = JsonDocument.Parse(await old.Content.ReadAsStringAsync());
                Check.That(old.StatusCode == HttpStatusCode.OK && firstJson.RootElement.GetProperty("id").GetGuid() == oldJson.RootElement.GetProperty("id").GetGuid()
                    && oldJson.RootElement.GetProperty("itemType").GetString() == type, "Legacy identity or response changed");
            }
            Check.That(host.Repository.Items.Count == 2 && host.Remote.Calls == 4, "Routes duplicated items or remote calls");
            // Four requests above plus six below exhaust the SAME existing write budget.
            for (var i = 0; i < 6; i++)
            {
                using var response = await host.Client.PostAsync("/api/v1/items/import/126", null);
                Check.That(response.StatusCode == HttpStatusCode.OK, "Write budget reduced");
            }
            using var limited = await host.Client.PostAsJsonAsync("/api/v1/catalog/imports", new
            {
                itemType = "Asset",
                robloxId = 126
            });
            Check.That(limited.StatusCode == HttpStatusCode.TooManyRequests && host.Remote.Calls == 10, "New route bypassed shared budget");
        });
        suite.AsyncTest("ImportRoutes_RejectInvalidAndUnauthorizedBeforeImporter", async () =>
        {
            await using var host = await Host.Start();
            host.Client.DefaultRequestHeaders.Remove("X-Liro-Admin-Key");
            foreach (var path in new[] { "/api/v1/catalog/imports", "/api/v1/items/import/126", "/api/v1/items/import-bundle/126" })
            {
                using var denied = await host.Client.PostAsJsonAsync(path, new
                {
                    itemType = "Asset",
                    robloxId = 126
                });
                Check.That(denied.StatusCode == HttpStatusCode.Unauthorized, "Import allowed without administrative key");
            }
            host.Client.DefaultRequestHeaders.Add("X-Liro-Admin-Key", Host.Key);
            foreach (var body in new[] { "{}", "null", "{\"itemType\":\"Unknown\",\"robloxId\":126}", "{\"itemType\":1,\"robloxId\":126}", "{\"itemType\":\"Asset\",\"robloxId\":0}", "{\"itemType\":\"Bundle\",\"robloxId\":-1}" })
            {
                using var invalid = await host.Client.PostAsync("/api/v1/catalog/imports", new StringContent(body, Encoding.UTF8, "application/json"));
                Check.That(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid import body not rejected");
            }
            Check.That(host.Remote.Calls == 0 && host.Repository.Items.Count == 0, "Invalid request reached importer");
            using var missing = await host.Client.PostAsJsonAsync("/api/v1/catalog/imports", new
            {
                itemType = "Asset",
                robloxId = 999
            });
            Check.That(missing.StatusCode == HttpStatusCode.NotFound && host.Remote.Calls == 1, "Missing remote item contract changed");
        });
    }

    private sealed class Host(WebApplication app, HttpClient client, Repository repository, Remote remote) : IAsyncDisposable
    {
        public const string Key = "test-only-key-abcdefghijklmnopqrstuvwxyz";
        public HttpClient Client => client;
        public Repository Repository => repository;
        public Remote Remote => remote;
        public static async Task<Host> Start()
        {
            var repository = new Repository();
            var remote = new Remote();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration["Security:AdminApiKey"] = Key;
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            builder.Services.AddSingleton<IItemMarketSummaryRepository>(new ItemPriceTests.RapStore());
            builder.Services.AddControllers().AddApplicationPart(typeof(ItemsController).Assembly);
            builder.Services.AddSingleton(new ItemService(repository));
            builder.Services.AddSingleton(new ImportRobloxItemService(remote, repository, new Thumbnail(), NullLogger<ImportRobloxItemService>.Instance, new Failures(), new CatalogRefreshPolicy()));
            var app = builder.Build();
            // Test host only: emulate the secure transport required by the real auth handler.
            // Existing Admin authentication tests separately reject a plain HTTP scheme.
            app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapControllers();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Liro-Admin-Key", Key);
            return new(app, client, repository, remote);
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Thumbnail : IRobloxThumbnailClient
    {
        public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetThumbnailAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
    private sealed class Failures : ICatalogImportFailureStore
    {
        public Task ResolveAsync(CatalogItemKey key, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Remote : IRobloxCatalogClient
    {
        public int Calls;
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => GetItemAsync(new CatalogItemKey(id), ct);
        public Task<RobloxCatalogItem?> GetItemAsync(CatalogItemKey key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<RobloxCatalogItem?>(key.Id == 999 ? null : new(key.Id, "Test item", null, ItemMarketStatus.Normal)
            {
                ItemType = key.Type,
                Details = new()
                {
                    CreatorId = 1,
                    CreatorType = "User"
                },
                ObservedAtUtc = DateTime.UtcNow
            });
        }
    }
    private sealed class Repository : IItemRepository
    {
        public List<Item> Items { get; } = [];
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(x => x.RobloxAssetId == key.Id && x.ItemType == key.Type));
        public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(Items.Any(x => x.RobloxAssetId == key.Id && x.ItemType == key.Type));
        public Task AddAsync(Item item, CancellationToken ct = default)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Item item, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeferRefreshAsync(CatalogItemKey key, DateTime time, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CatalogItemKey key, CancellationToken ct = default) => throw new InvalidOperationException("No deletion expected");
        public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime time, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
    }
}
