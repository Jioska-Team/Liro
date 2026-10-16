using System.Net;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Liro.Workers.Catalog;
namespace Liro.RegressionTests;

internal static class CoverageTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Coverage resumes typed cursor and retains a saturated leaf as incomplete coverage", async () =>
        {
            var config = SmallConfig();
            config.CoverageAssetTypeIds = [8];
            config.CoverageMaxDepth = 0;
            var store = new Checkpoints();
            store.States[Key(config, new(CatalogSearchScope.All))] = new(null, true)
            {
                CoverageStatus = "SearchExhausted"
            };
            var hat = new CatalogSearchPartition(CatalogSearchScope.All) { AssetTypeId = 8 };
            store.States[Key(config, hat)] = new("resume", false)
            {
                ItemsSeen = 999,
                PagesProcessed = 8
            };
            var discovery = new Discovery((partition, cursor) =>
            {
                Check.That(partition.AssetTypeId == 8 && cursor == "resume", "Typed checkpoint not resumed");
                return new([], null)
                {
                    ReturnedCount = 1
                };
            });
            await RunAsync(config, store, discovery);
            var state = store.States[Key(config, hat)];
            Check.That(state.Completed && state.CoverageStatus == "PossiblyTruncated" && state.ItemsSeen == 1000 && state.PagesProcessed == 9, "Saturation marked as complete coverage or progress lost");
        });
        suite.AsyncTest("Completed saturated partitions restore their children after restart", async () =>
        {
            var config = SmallConfig();
            config.CoverageAssetTypeIds = [8];
            var store = new Checkpoints();
            var hat = new CatalogSearchPartition(CatalogSearchScope.All) { AssetTypeId = 8 };
            store.States[Key(config, new(CatalogSearchScope.All))] = new(null, true)
            {
                CoverageStatus = "SearchExhausted"
            };
            store.States[Key(config, hat)] = new(null, true)
            {
                ItemsSeen = 1000,
                CoverageStatus = "PossiblyTruncated"
            };
            var calls = 0;
            var discovery = new Discovery((partition, cursor) => { calls++; Check.That(partition.Key != hat.Key, "Completed parent fetched again"); return new([], null); });
            await RunAsync(config, store, discovery);
            var expected = CatalogCoveragePlanner.Expand(hat, config);
            Check.That(calls == expected.Count && expected.All(x => store.States[Key(config, x)].Completed), "Children omitted");
            await RunAsync(config, store, discovery);
            Check.That(calls == expected.Count, "Completed children fetched again after restart");
        });
        suite.AsyncTest("Coverage failure retains cursor and clears error after successful retry", async () =>
        {
            var config = SmallConfig();
            var store = new Checkpoints();
            var key = Key(config, new(CatalogSearchScope.All));
            store.States[key] = new("saved", false)
            {
                ItemsSeen = 120,
                PagesProcessed = 1
            };
            var calls = 0;
            var discovery = new Discovery((partition, cursor) =>
            {
                calls++;
                Check.That(cursor == "saved", "Failure advanced cursor");
                if (calls == 1)
                {
                    throw new HttpRequestException("upstream unavailable", null, HttpStatusCode.InternalServerError);
                }
                Check.That(store.States[key].CoverageStatus == "Failed" && store.States[key].LastError is not null, "Failure was invisible");
                return new([], null);
            });
            await RunAsync(config, store, discovery);
            Check.That(calls == 2 && store.States[key].CoverageStatus == "SearchExhausted" && store.States[key].LastError is null && store.States[key].PagesProcessed == 2, "Recovery reset or duplicated progress");
        });
        suite.AsyncTest("Coverage client sends typed price and encoded keyword filters", async () =>
        {
            using var http = new HttpClient(new StubHttp(request =>
            {
                var query = request.RequestUri!.Query;
                Check.That(query.Contains("AssetTypeIds=8") && query.Contains("MinPrice=0") && query.Contains("MaxPrice=0") && query.Contains("Keyword=valkyrie%26test") && query.Contains("CreatorTargetId=1") && query.Contains("IncludeNotForSale=true"), "Partition filters were lost or injected");
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":17517206952,"itemType":"Asset","assetType":8},{"id":7781687598,"itemType":"Asset","assetType":8},{"id":928840419,"itemType":"Asset","assetType":8}],"nextPageCursor":null}""")
                };
            }))
            {
                BaseAddress = new("https://catalog.roblox.com/")
            };
            var client = new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions()));
            var page = await client.GetPartitionPageAsync(new(CatalogSearchScope.All)
            {
                AssetTypeId = 8,
                MinPrice = 0,
                MaxPrice = 0,
                Keyword = "valkyrie&test"
            }, null, default);
            Check.That(page.Items.Select(x => x.Id).SequenceEqual(new long[] { 17517206952, 7781687598, 928840419 }) && page.ReturnedCount == 3, "Missing Valkyrie identities discarded");
        });
        suite.AsyncTest("Coverage checkpoint write failure does not commit a completed page", async () =>
        {
            var config = SmallConfig();
            var store = new Checkpoints { RejectNextSave = true };
            var key = Key(config, new(CatalogSearchScope.All));
            store.States[key] = new("old-cursor", false)
            {
                ItemsSeen = 120,
                PagesProcessed = 1
            };
            var calls = 0;
            var discovery = new Discovery((partition, cursor) =>
            {
                calls++;
                Check.That(cursor == "old-cursor" && !store.States[key].Completed, "Failed checkpoint moved progress");
                return new([], null);
            });
            await RunAsync(config, store, discovery);
            Check.That(calls == 2 && store.States[key].Completed && store.States[key].PagesProcessed == 2, "Page was lost or counted twice");
        });
        suite.AsyncTest("Coverage rejects provider results outside requested asset type", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":1,"itemType":"Asset","assetType":41}],"nextPageCursor":null}""") })) { BaseAddress = new("https://catalog.roblox.com/") };
            await Check.ThrowsAsync<System.Text.Json.JsonException>(() => new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions()))
                .GetPartitionPageAsync(new(CatalogSearchScope.All)
                {
                    AssetTypeId = 8
                }, null, default));
        });
        suite.Test("Coverage migration preserves catalog and old checkpoints", () =>
        {
            var migration = new AddCatalogCoverageProgress();
            Check.That(migration.UpOperations.Count == 6 && migration.UpOperations.All(x => x is AddColumnOperation column && column.Table == "CatalogCheckpoints"), "Coverage migration changes stored items or existing progress");
            Check.That(migration.UpOperations.OfType<AddColumnOperation>().Single(x => x.Name == "CoverageStatus").DefaultValue?.ToString() == "Unassessed", "Old checkpoints incorrectly marked fully covered");
        });
        suite.Test("Coverage partitions isolate types prices keywords and sort order", () =>
        {
            var hat = new CatalogSearchPartition(CatalogSearchScope.All) { AssetTypeId = 8 };
            var bundle = new CatalogSearchPartition(CatalogSearchScope.All) { BundleTypeId = 8 };
            Check.That(new[] { hat.Key, bundle.Key, (hat with { MinPrice = 0, MaxPrice = 0 }).Key, (hat with { Keyword = "valkyrie" }).Key, (hat with { SortType = 3 }).Key }.Distinct().Count() == 5, "Checkpoints collide");
            var config = new RobloxCatalogOptions();
            var roots = CatalogCoveragePlanner.Roots(config);
            Check.That(roots.Any(x => x.AssetTypeId == 8) && roots.Any(x => x.BundleTypeId == 1) && roots.Any(x => x.Keyword == "valkyrie"), "Coverage roots missing");
        });
        suite.Test("Saturated coverage expands into bounded nonoverlapping price ranges", () =>
        {
            var config = new RobloxCatalogOptions();
            var hat = new CatalogSearchPartition(CatalogSearchScope.All) { AssetTypeId = 8 };
            var children = CatalogCoveragePlanner.Expand(hat, config);
            var prices = children.Where(x => x.SortType == 0).OrderBy(x => x.MinPrice).ToArray();
            Check.That(prices[0].MinPrice == 0 && prices[^1].MaxPrice == int.MaxValue, "Price range endpoints lost");
            Check.That(prices.Zip(prices.Skip(1)).All(x => (long)x.First.MaxPrice! + 1 == x.Second.MinPrice), "Price ranges overlap or have gaps");
            var split = CatalogCoveragePlanner.Expand(hat with
            {
                MinPrice = 1,
                MaxPrice = 100,
                Depth = 1
            }, config).Where(x => x.SortType == 0).ToArray();
            Check.That(split.Length == 2 && split[0].MaxPrice == 50 && split[1].MinPrice == 51, "Bisection wrong");
            Check.That(CatalogCoveragePlanner.Expand(hat with
            {
                Depth = config.CoverageMaxDepth
            }, config).Count == 0, "Unbounded expansion");
            Check.That(CatalogCoveragePlanner.Expand(hat with
            {
                MinPrice = 0,
                MaxPrice = 0
            }, config).All(x => x.SortType != 0), "Zero price range loops forever");
        });
    }
    private static RobloxCatalogOptions SmallConfig() => new() { SearchScopes = [CatalogSearchScope.All], CoverageAssetTypeIds = [], CoverageBundleTypeIds = [], CoverageKeywords = [] };
    private static string Key(RobloxCatalogOptions options, CatalogSearchPartition partition) => options.CoverageScopeKey + ":" + partition.Key;
    private static async Task RunAsync(RobloxCatalogOptions config, Checkpoints checkpoints, Discovery discovery)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
        services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
        using var provider = services.BuildServiceProvider();
        using var worker = new CatalogBootstrapWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CatalogBootstrapWorker>.Instance,
            Options.Create(new CatalogWorkerOptions { ItemDelay = TimeSpan.Zero, PageDelay = TimeSpan.Zero, RetryDelay = TimeSpan.FromMilliseconds(1) }), Options.Create(config));
        await worker.StartAsync(default);
        try
        {
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(default); }
    }
    private sealed class Checkpoints : ICatalogCheckpointStore
    {
        public Dictionary<string, CatalogCheckpoint> States { get; } = [];
        public bool RejectNextSave
        {
            get; set;
        }
        public Task<CatalogCheckpoint> LoadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(States.GetValueOrDefault(key, new(null, false)));
        public Task SaveAsync(string key, CatalogCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (RejectNextSave)
            {
                RejectNextSave = false;
                throw new InvalidOperationException("simulated checkpoint write failure");
            }
            States[key] = checkpoint;
            return Task.CompletedTask;
        }
    }
    private sealed class Discovery(Func<CatalogSearchPartition, string?, RobloxCatalogDiscoveryPage> get) : IRobloxCatalogDiscoveryClient
    {
        public Task<RobloxCatalogDiscoveryPage> GetPartitionPageAsync(CatalogSearchPartition partition, string? cursor, CancellationToken cancellationToken) => Task.FromResult(get(partition, cursor));
        public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}



