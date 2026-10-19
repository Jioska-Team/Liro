using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Liro.CatalogWorker.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class WorkerTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Discovery skips existing items regardless of refresh schedule", async () =>
        {
            foreach (var state in new[] { "unchecked", "overdue", "fresh" })
            {
                var item = Fixtures.Item();
                if (state != "unchecked")
                {
                    item.MarkChecked(state == "overdue" ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow, TimeSpan.FromMinutes(30));
                }
                var repository = new MemoryRepository(item);
                var catalog = new CountingCatalog();
                using var provider = CreateProvider(repository, new RecoveringDiscovery(), catalog);
                var processor = provider.GetRequiredService<CatalogImportProcessor>();
                var attempted = await processor.ProcessAsync(1, true, CancellationToken.None);
                Check.That(!attempted, "Skipped item must not require the upstream item delay");
                Check.That(catalog.Calls == 0 && repository.Saves == 0, "Discovery queried or changed an existing " + state + " item");
            }
        });
        suite.AsyncTest("Discovery imports unknown items and refresh updates existing items", async () =>
        {
            var repository = new MemoryRepository(Fixtures.Item());
            var catalog = new CountingCatalog();
            using var provider = CreateProvider(repository, new RecoveringDiscovery(), catalog);
            var processor = provider.GetRequiredService<CatalogImportProcessor>();
            var imported = await processor.ProcessAsync(2, true, CancellationToken.None);
            var refreshed = await processor.ProcessAsync(1, false, CancellationToken.None);
            Check.That(imported && refreshed, "Actual upstream attempts must retain their pacing");
            Check.That(catalog.Calls == 2 && repository.SavedAssetIds.SequenceEqual(new long[] { 2, 1 }), "Discovery/refresh responsibilities were mixed");
        });
        suite.AsyncTest("Bootstrap does not wait item delay for existing items", async () =>
        {
            var repository = new MemoryRepository(Fixtures.Item());
            using var provider = CreateProvider(repository, new RecoveringDiscovery(), new CatalogStub());
            using var worker = new CatalogBootstrapWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CatalogBootstrapWorker>.Instance,
                Options.Create(new CatalogWorkerOptions { RetryDelay = TimeSpan.FromMilliseconds(10), PageDelay = TimeSpan.Zero, ItemDelay = TimeSpan.FromHours(1) }),
                Options.Create(new RobloxCatalogOptions { CoverageAssetTypeIds = [], CoverageBundleTypeIds = [], CoverageKeywords = [], CoverageAdditionalFiltersEnabled = false, SearchScopes = [CatalogSearchScope.All] }));
            await RunToCompletionAsync(worker);
            Check.That(repository.Saves == 0, "Bootstrap modified an existing item");
        });
        suite.AsyncTest("Recent discovery polls newest page before saved backlog and isolates scopes", async () =>
        {
            var config = new RobloxCatalogOptions { CoverageAssetTypeIds = [], CoverageBundleTypeIds = [], CoverageKeywords = [], CoverageAdditionalFiltersEnabled = false };
            var checkpoints = new ScopedCheckpoints();
            foreach (var scope in config.SearchScopes)
            {
                checkpoints.States[config.ScopeKey + ":recent:" + scope] = new("tail-" + scope, false);
            }
            var discovery = new ScopedDiscovery();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            using var provider = services.BuildServiceProvider();
            using var worker = new CatalogDiscoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CatalogDiscoveryWorker>.Instance,
                Options.Create(new CatalogWorkerOptions { DiscoveryPages = 1, PageDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero, CycleInterval = TimeSpan.FromHours(1) }), Options.Create(config));
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await checkpoints.BothSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check.That(discovery.Calls.SequenceEqual(new[] { "All:head", "All:tail-All", "Collectibles:head", "Collectibles:tail-Collectibles" }), "Newest page or independent backlog was skipped");
                Check.That(checkpoints.States.Values.All(x => x.Cursor == "next-tail"), "Head polling overwrote backlog progress");
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        });
        suite.AsyncTest("Completed legacy checkpoint does not block new bootstrap scopes", async () =>
        {
            var checkpoints = new ScopedCheckpoints();
            checkpoints.States["catalog:1:User:1:30"] = new(null, true);
            var discovery = new ScopedDiscovery();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            using var provider = services.BuildServiceProvider();
            var config = new RobloxCatalogOptions { CoverageAssetTypeIds = [], CoverageBundleTypeIds = [], CoverageKeywords = [], CoverageAdditionalFiltersEnabled = false };
            using var worker = new CatalogBootstrapWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CatalogBootstrapWorker>.Instance,
                Options.Create(new CatalogWorkerOptions { PageDelay = TimeSpan.Zero }), Options.Create(config));
            await RunToCompletionAsync(worker);
            Check.That(discovery.Calls.SequenceEqual(new[] { "All:head", "Collectibles:head" }), "Legacy checkpoint suppressed a new scope");
            Check.That(config.SearchScopes.All(x => checkpoints.States[config.CoverageScopeKey + ":" + new CatalogSearchPartition(x).Key].Completed), "New scope was not completed independently");
        });
        suite.AsyncTest("Bootstrap recovers after an upstream 500", async () =>
        {
            var repository = new MemoryRepository(new Liro.Domain.Catalog.Entities.Item(99, "Other"));
            using var provider = CreateProvider(repository, new RecoveringDiscovery(), new CatalogStub());
            using var worker = CreateWorker(provider);

            await RunToCompletionAsync(worker);

            Check.That(repository.SavedAssetIds.SequenceEqual(new long[] { 1 }),
                "Bootstrap did not save exactly the requested asset after recovering");
        });

        suite.AsyncTest("Poison asset does not block later bootstrap pages", async () =>
        {
            var repository = new MemoryRepository(Fixtures.Item());
            using var provider = CreateProvider(repository, new TwoPageDiscovery(), new PoisonCatalog());
            using var worker = CreateWorker(provider);

            await RunToCompletionAsync(worker);

            Check.That(repository.SavedAssetIds.SequenceEqual(new long[] { 2 }),
                "Bootstrap did not save the healthy asset on the following page");
        });
    }

    private static ServiceProvider CreateProvider(MemoryRepository repository, IRobloxCatalogDiscoveryClient discovery, IRobloxCatalogClient catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICatalogCheckpointStore>(new MemoryCheckpointStore());
        services.AddSingleton<IItemRepository>(repository);
        services.AddSingleton(discovery);
        services.AddSingleton(catalog);
        services.AddSingleton<IRobloxThumbnailClient>(new PendingThumbnail());
        services.AddSingleton<ILogger<ImportRobloxItemService>>(NullLogger<ImportRobloxItemService>.Instance);
        services.AddSingleton<ILogger<CatalogImportProcessor>>(NullLogger<CatalogImportProcessor>.Instance);
        services.AddSingleton<ICatalogImportFailureStore>(new MemoryFailureStore());
        services.AddSingleton(new CatalogRefreshPolicy());
        services.AddScoped<ImportRobloxItemService>();
        services.AddScoped<CatalogImportProcessor>();
        return services.BuildServiceProvider();
    }

    private static CatalogBootstrapWorker CreateWorker(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<CatalogBootstrapWorker>.Instance,
        Options.Create(new CatalogWorkerOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
            ItemDelay = TimeSpan.Zero,
            PageDelay = TimeSpan.Zero
        }),
        Options.Create(new RobloxCatalogOptions { CoverageAssetTypeIds = [], CoverageBundleTypeIds = [], CoverageKeywords = [], CoverageAdditionalFiltersEnabled = false, SearchScopes = [Liro.Application.RobloxIntegration.Models.CatalogSearchScope.All] }));

    private static async Task RunToCompletionAsync(CatalogBootstrapWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var execution = worker.ExecuteTask ?? throw new InvalidOperationException("Worker did not start.");
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(shutdown.Token);
        }
    }
    private sealed class ScopedCheckpoints : ICatalogCheckpointStore
    {
        public Dictionary<string, CatalogCheckpoint> States { get; } = [];
        public TaskCompletionSource BothSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int saved;
        public Task<CatalogCheckpoint> LoadAsync(string scope, CancellationToken cancellationToken) => Task.FromResult(States.GetValueOrDefault(scope, new(null, false)));
        public Task SaveAsync(string scope, CatalogCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            States[scope] = checkpoint;
            if (++saved == 2)
            {
                BothSaved.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }
    private sealed class ScopedDiscovery : IRobloxCatalogDiscoveryClient
    {
        public List<string> Calls { get; } = [];
        public Task<RobloxCatalogDiscoveryPage> GetPageAsync(CatalogSearchScope scope, bool recent, string? cursor, CancellationToken cancellationToken)
        {
            Calls.Add(scope + ":" + (cursor ?? "head"));
            return Task.FromResult(new RobloxCatalogDiscoveryPage([], recent ? cursor is null ? "head-next" : "next-tail" : null));
        }
        public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CountingCatalog : IRobloxCatalogClient
    {
        public int Calls
        {
            get; private set;
        }
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return new CatalogStub().GetItemAsync(id, cancellationToken);
        }
    }
}






