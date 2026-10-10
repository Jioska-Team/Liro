using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Roblox;
using Liro.Workers.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class WorkerTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Bootstrap recovers after an upstream 500", async () =>
        {
            var repository = new MemoryRepository(Fixtures.Item());
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
        Options.Create(new RobloxCatalogOptions()));

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
}
