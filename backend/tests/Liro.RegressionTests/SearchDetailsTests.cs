using System.Net;
using System.Text.Json;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Roblox;
using Liro.CatalogWorker.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class SearchDetailsTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Bundle discovery keeps only valid assets from official bundles", async () =>
        {
            var bundle = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Entry(10, "Bundle")))!;
            bundle["bundledItems"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":10,"type":"Asset"},{"id":20,"type":"Asset"},{"id":20,"type":"Asset"},{"id":30,"type":"UserOutfit"},{"id":0,"type":"Asset"},{"id":-1,"type":"Asset"},{"id":"bad","type":"Asset"},null,{"id":40}]""");
            var foreign = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Entry(11, "Bundle", creator: 99)))!;
            foreign["bundledItems"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":99,"type":"Asset"}]""");
            using var http = Http([bundle, foreign, Entry(20)]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, false, null, default);
            Check.That(page.BundledAssets.SequenceEqual(new CatalogItemKey[] { new(10) }), "Invalid, foreign, outfit or already listed assets were discovered");
            Check.That(page.ReturnedCount == 3 && page.Items.Count == 3 && page.Items.Contains(new(10, CatalogItemType.Bundle)), "Component count contaminated cursor coverage or bundle identity");
            Check.That(!page.Details.ContainsKey(new(10)), "Bundle ownership was copied to the asset");
        });
        suite.AsyncTest("Bundle components confirm creators and replay without duplicate inserts", async () =>
        {
            var repo = new Repository();
            var catalog = new FallbackCatalog { ForeignId = 99, OffSale = true };
            using var provider = Provider(repo, catalog, allowImages: true);
            var bundle = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Entry(10, "Bundle")))!;
            bundle["bundledItems"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":10,"type":"Asset"},{"id":10,"type":"Asset"},{"id":99,"type":"Asset"}]""");
            using var http = Http([bundle]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, false, null, default);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var first = await CatalogSearchPageProcessor.ProcessAsync(scopes, page, true, new()
            {
                ItemDelay = TimeSpan.Zero
            }, default);
            var second = await CatalogSearchPageProcessor.ProcessAsync(scopes, page, true, new()
            {
                ItemDelay = TimeSpan.Zero
            }, default);
            Check.That(first.Added == 2 && first.Failed == 0 && second.Added == 0 && second.Failed == 0 && repo.Saves == 2, "Replay duplicated writes or failed to import bundle components");
            Check.That(repo.Items.Keys.ToHashSet().SetEquals(new CatalogItemKey[] { new(10), new(10, CatalogItemType.Bundle) }) && !repo.Items.ContainsKey(new(99)), "Foreign creator imported or typed identities collided");
            Check.That(repo.Items[new(10)].MarketState?.Data.IsOffSale == true, "Off-sale component was dropped or lost its sale state");
            Check.That(catalog.Calls.Count(x => x.Id == 10) == 1 && catalog.Calls.Any(x => x.Id == 99), "Known component refetched or foreign component not independently confirmed");
        });
        suite.AsyncTest("Limited monitor does not fetch bundle discovery hints", async () =>
        {
            var repo = new Repository();
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog);
            using var http = Http([Entry(10, "Bundle")]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, false, null, default);
            page = page with
            {
                BundledAssets = new CatalogItemKey[] { new(20) }
            };
            await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default, fetchFallback: false);
            Check.That(catalog.Calls.Count == 0 && repo.Items.Count == 1, "Priority monitor blocked on component details");
        });
        suite.AsyncTest("Search page imports 120 typed items without extra details or images", async () =>
        {
            var repo = new Repository();
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog);
            var requests = 0;
            using var http = Http(Enumerable.Range(1, 120).Select(id => Entry(id, id == 120 ? "Bundle" : "Asset")), () => requests++);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, true, null, default);
            var outcome = await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default);
            Check.That(requests == 1 && catalog.Calls.Count == 0 && outcome.Added == 120 && outcome.Failed == 0 && repo.Saves == 120,
                "Search details were discarded or extra HTTP calls were made");
            Check.That(repo.Items[new(120, CatalogItemType.Bundle)].BundleTypeId == 1 && repo.Items[new(1)].AssetTypeId == 8,
                "Typed metadata was lost");
            Check.That(repo.Items[new(1)].MarketState?.Source == "RobloxCatalogSearchV2" && repo.Items[new(1)].MarketState?.Data.PrimaryPriceRobux == 10,
                "Search market observation lost price or provenance");
        });
        suite.AsyncTest("Search corrects known limited and skips unchanged writes", async () =>
        {
            var item = new Item(1, "Old");
            var repo = new Repository(item);
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog);
            using var http = Http([Entry(1)]);
            var client = Client(http);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            await CatalogSearchPageProcessor.ProcessAsync(scopes, await client.GetPageAsync(CatalogSearchScope.Collectibles, true, null, default), false, new(), default);
            var checkedAt = item.LastCheckedAt;
            var observations = item.PendingMarketSnapshots.Count;
            await CatalogSearchPageProcessor.ProcessAsync(scopes, await client.GetPageAsync(CatalogSearchScope.Collectibles, true, null, default), false, new(), default);
            Check.That(item.MarketStatus == ItemMarketStatus.Limited && item.Restrictions.SequenceEqual(new[] { "Limited" })
                && item.DomainEvents.Count == 1 && repo.Saves == 1 && item.PendingMarketSnapshots.Count == observations
                && item.LastCheckedAt == checkedAt && catalog.Calls.Count == 0, "Known item skipped, unchanged data rewritten or duplicate event emitted");
        });
        suite.AsyncTest("Limited monitor applies search details without confirmation requests", async () =>
        {
            var item = new Item(1, "Normal");
            var repo = new Repository(item);
            var catalog = new FallbackCatalog();
            using var http = Http([Entry(1)]);
            using var provider = Provider(repo, catalog, discovery: Client(http));
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()),
                Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.PollAsync(default);
            Check.That(item.MarketStatus == ItemMarketStatus.Limited && repo.Saves == 1 && catalog.Calls.Count == 0,
                "Monitor queued complete details instead of applying the search observation");
        });
        suite.AsyncTest("Search confirms only incomplete identities through fallback", async () =>
        {
            var repo = new Repository();
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog, allowImages: true);
            using var http = Http([Entry(1), new { id = 2, itemType = "Asset" }]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, true, null, default);
            var result = await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new()
            {
                ItemDelay = TimeSpan.Zero
            }, default);
            Check.That(result.Added == 2 && result.Failed == 0 && catalog.Calls.SequenceEqual(new CatalogItemKey[] { new(2) }), "Healthy item redundantly fetched or incomplete item lost");
        });
        suite.AsyncTest("Search excludes foreign creator and rejects duplicate details", async () =>
        {
            var repo = new Repository();
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog);
            using var http = Http([Entry(1, creator: 99), Entry(2), Entry(2)]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, true, null, default);
            await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default, fetchFallback: false);
            Check.That(repo.Saves == 0 && page.Details[new(2)].Item is null && page.Details[new(2)].Error is not null && catalog.Calls.Count == 0,
                "Foreign creator or ambiguous duplicated observation was accepted");
        });
        suite.AsyncTest("Older search response cannot overwrite a newer refresh", async () =>
        {
            var item = new Item(1, "Current", null, ItemMarketStatus.LimitedUnique);
            item.MarkChecked(DateTime.UtcNow, TimeSpan.FromMinutes(30));
            var repo = new Repository(item);
            using var provider = Provider(repo, new FallbackCatalog());
            using var http = Http([Entry(1)]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, true, null, default);
            var result = page.Details[new(1)];
            page = page with
            {
                Details = new Dictionary<CatalogItemKey, RobloxCatalogResult> { [new(1)] = result with { Item = result.Item! with { ObservedAtUtc = item.LastCheckedAt!.Value.AddMinutes(-1) } } }
            };
            await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default);
            Check.That(item.Name == "Current" && item.MarketStatus == ItemMarketStatus.LimitedUnique && repo.Saves == 0, "Late search reverted newer details");
        });
        suite.AsyncTest("Failed page batch disposes scope before individual recovery without refetching", async () =>
        {
            var writer = new FailingWriter();
            var repo = new Repository { OnRead = () => Check.That(writer.Disposed, "Fallback reused batch scope") };
            var catalog = new FallbackCatalog();
            using var provider = Provider(repo, catalog, writer: writer);
            using var http = Http([Entry(1), Entry(2)]);
            var page = await Client(http).GetPageAsync(CatalogSearchScope.All, true, null, default);
            var result = await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default);
            Check.That(writer.Calls == 1 && writer.Disposed && repo.Saves == 2 && result.Added == 2 && result.Failed == 0 && catalog.Calls.Count == 0,
                "Batch rollback lost observations, double counted inserts or refetched details");
        });
    }

    private static object Entry(int id, string type = "Asset", int creator = 1) => new
    {
        id,
        itemType = type,
        name = "Item " + id,
        creatorTargetId = creator,
        creatorType = "User",
        creatorName = "Roblox",
        assetType = type == "Asset" ? (int?)8 : null,
        bundleType = type == "Bundle" ? (int?)1 : null,
        itemRestrictions = new[] { "Limited" },
        itemCreatedUtc = "2025-01-01T00:00:00Z",
        description = "Details",
        price = 10,
        isOffSale = false
    };
    private static HttpClient Http(IEnumerable<object> entries, Action? requested = null) => new(new StubHttp(_ =>
    {
        requested?.Invoke();
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                data = entries
            }))
        };
    }))
    {
        BaseAddress = new("https://catalog.roblox.com/")
    };
    private static RobloxCatalogDiscoveryClient Client(HttpClient http) => new(http, Options.Create(new RobloxCatalogOptions()));
    private static ServiceProvider Provider(Repository repo, FallbackCatalog catalog, bool allowImages = false, IRobloxCatalogDiscoveryClient? discovery = null, ICatalogPageWriter? writer = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IItemRepository>(repo);
        services.AddSingleton<IRobloxCatalogClient>(catalog);
        services.AddSingleton<IRobloxThumbnailClient>(allowImages ? new PendingThumbnail() : new ForbiddenImages());
        services.AddSingleton<ICatalogImportFailureStore>(new Failures());
        services.AddSingleton(new CatalogRefreshPolicy());
        services.AddLogging();
        services.AddScoped<ImportRobloxItemService>();
        services.AddScoped<CatalogImportProcessor>();
        if (writer is not null)
            services.AddScoped<ICatalogPageWriter>(_ => writer);
        if (discovery is not null)
        {
            services.AddSingleton(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(new MemoryCheckpointStore());
            services.AddSingleton<ICatalogPriorityQueue>(new NoConfirmationQueue());
        }
        return services.BuildServiceProvider();
    }
    private sealed class NoConfirmationQueue : ICatalogPriorityQueue
    {
        public Task EnqueueAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime time, CancellationToken ct)
        {
            Check.That(keys.Count == 0, "Complete observation enqueued for redundant confirmation");
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CatalogPriorityEntry>> GetDueAsync(DateTime now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(CatalogPriorityEntry entry, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogQueueHealth> GetHealthAsync(DateTime now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class FailingWriter : ICatalogPageWriter, IDisposable
    {
        public bool Disposed;
        public int Calls;
        public Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> observations, bool onlyNew, CancellationToken ct)
        {
            Calls++;
            throw new CatalogBatchWriteException(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("conflict"));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class ForbiddenImages : IRobloxThumbnailClient
    {
        public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Search must not fetch images");
    }
    private sealed class Failures : ICatalogImportFailureStore
    {
        public Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken ct) => Task.FromResult(true);
        public Task ResolveAsync(CatalogItemKey key, CancellationToken ct) => Task.CompletedTask;
        public Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken ct) => throw new Exception(error);
        public Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
    }
    private sealed class FallbackCatalog : IRobloxCatalogClient
    {
        public bool OffSale
        {
            get; init;
        }
        public long? ForeignId
        {
            get; init;
        }
        public List<CatalogItemKey> Calls { get; } = [];
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default)
        {
            Calls.Add(new(id));
            return Task.FromResult<RobloxCatalogItem?>(new(id, "Fallback", null, ItemMarketStatus.Normal)
            {
                ObservedAtUtc = DateTime.UtcNow,
                Market = OffSale ? new()
                {
                    IsOffSale = true
                } : null,
                Details = new()
                {
                    CreatorId = id == ForeignId ? 99 : 1,
                    CreatorType = "User"
                }
            });
        }
    }
    private sealed class Repository(params Item[] initial) : IItemRepository
    {
        public Action? OnRead;
        public Dictionary<CatalogItemKey, Item> Items { get; } = initial.ToDictionary(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType));
        public int Saves;
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default)
        {
            OnRead?.Invoke();
            return Task.FromResult(Items.GetValueOrDefault(key));
        }
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.Values.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(Items.ContainsKey(key));
        public Task AddAsync(Item item, CancellationToken ct = default) => UpdateAsync(item, ct);
        public Task UpdateAsync(Item item, CancellationToken ct = default)
        {
            Saves++;
            Items[new(item.RobloxAssetId, item.ItemType)] = item;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CatalogItemKey key, CancellationToken ct = default)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeferRefreshAsync(CatalogItemKey key, DateTime time, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime time, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
    }
}
