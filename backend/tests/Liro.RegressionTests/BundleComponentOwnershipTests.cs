using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.CatalogWorker.Catalog;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class BundleComponentOwnershipTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var scenario in new[]
        {
            (Name: "Components_OfficialBatchSkipsOwnershipQueries", Creator: 1L, Type: "User", Linked: false, Accepted: true, Count: 10),
            (Name: "Components_LinkedUserRequiresOwnershipQuery", Creator: 99L, Type: "User", Linked: true, Accepted: true, Count: 1),
            (Name: "Components_LinkedGroupRequiresOwnershipQuery", Creator: 1L, Type: "Group", Linked: true, Accepted: true, Count: 1),
            (Name: "Components_UnlinkedUserIsRejected", Creator: 99L, Type: "User", Linked: false, Accepted: false, Count: 1),
            (Name: "Components_UnlinkedGroupIsRejected", Creator: 1L, Type: "Group", Linked: false, Accepted: false, Count: 1)
        })
        {
            suite.AsyncTest(scenario.Name, async () =>
            {
                var keys = Enumerable.Range(10, scenario.Count).Select(id => new CatalogItemKey(id)).ToArray();
                var browse = new Browse(keys);
                var ownership = new Ownership(scenario.Linked);
                var catalog = new Catalog(scenario.Creator, scenario.Type);
                var writer = new Writer();
                var failures = new MemoryFailureStore();
                using var provider = new ServiceCollection()
                    .AddSingleton<ICatalogBrowseRepository>(browse)
                    .AddSingleton<IItemRepository>(ownership)
                    .AddSingleton<IRobloxCatalogClient>(catalog)
                    .AddSingleton<ICatalogPageWriter>(writer)
                    .AddSingleton<ICatalogImportFailureStore>(failures)
                    .BuildServiceProvider();
                using var worker = new BundleComponentsWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new CatalogWorkerOptions()), NullLogger<BundleComponentsWorker>.Instance);

                await worker.PollAsync(default);

                var official = scenario.Creator == 1 && scenario.Type == "User";
                Check.That(ownership.Queries.SequenceEqual(official ? [] : keys),
                    "Official components queried ownership, or external components bypassed ownership validation");
                Check.That(catalog.Calls == 1 && catalog.Requested.SequenceEqual(keys),
                    "Component details were requested repeatedly or with different identities");
                Check.That(writer.Keys.SequenceEqual(scenario.Accepted ? keys : []),
                    "Validated components were lost or unlinked components reached the writer");
                var failed = await failures.GetDueItemsAsync(DateTime.UtcNow, 10, default);
                Check.That(failed.SequenceEqual(scenario.Accepted ? [] : keys),
                    "Accepted component was marked failed or unlinked component was not rejected");
                Check.That(browse.Synchronizations == 2, "Relation reconciliation was changed");
            });
        }
    }

    private sealed class Catalog(long creator, string creatorType) : IRobloxCatalogClient
    {
        public int Calls;
        public CatalogItemKey[] Requested = [];
        public Task<RobloxCatalogItem?> GetItemAsync(long assetId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected individual catalog request");
        public Task<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>> GetItemsAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requested = keys.ToArray();
            return Task.FromResult<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>>(keys.ToDictionary(key => key, key =>
                new RobloxCatalogResult(new RobloxCatalogItem(key.Id, "Component", null, ItemMarketStatus.Normal)
                {
                    Details = new ItemCatalogDetails { CreatorId = creator, CreatorType = creatorType }
                }, null)));
        }
    }

    private sealed class Writer : ICatalogPageWriter
    {
        public List<CatalogItemKey> Keys { get; } = [];
        public Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> observations, bool onlyNew, CancellationToken cancellationToken)
        {
            Keys.AddRange(observations.Keys);
            return Task.FromResult(new CatalogPageWriteResult(observations.Count, 0, 0, []));
        }
    }

    private sealed class Browse(CatalogItemKey[] keys) : ICatalogBrowseRepository
    {
        public int Synchronizations;
        public Task<IReadOnlyList<CatalogItemKey>> GetBundlesDueAsync(DateTime before, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
        public Task<IReadOnlyList<CatalogItemKey>> GetMissingComponentsAsync(int limit, CancellationToken ct)
        {
            Check.That(limit == 10, "Missing-component batch limit changed");
            return Task.FromResult<IReadOnlyList<CatalogItemKey>>(keys);
        }
        public Task<IReadOnlyList<CatalogItemKey>> GetBundlesMissingThumbnailAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
        public Task SynchronizeRelationsAsync(CancellationToken ct) { Synchronizations++; return Task.CompletedTask; }
        public Task<CatalogPage> BrowseAsync(int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveBundleThumbnailAsync(long bundleId, string? url, DateTime checkedAt, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Ownership(bool linked) : IItemRepository
    {
        public List<CatalogItemKey> Queries { get; } = [];
        public Task<bool> IsOfficialBundleComponentAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
        {
            Queries.Add(key);
            return Task.FromResult(linked);
        }
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeferRefreshAsync(CatalogItemKey key, DateTime time, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(CatalogItemKey key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Item item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Item item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime time, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
