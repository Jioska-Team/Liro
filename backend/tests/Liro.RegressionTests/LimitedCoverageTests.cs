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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class LimitedCoverageTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("El lote conocido rechazado conserva los identificadores y el plazo de Roblox", async () =>
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            var watch = new Watch([new(1), new(2)]);
            using var provider = Provider(watch, new Details { RateLimitUntil = deadline }, new MemoryRepository(new Item(1, "One")));
            using var worker = Worker(provider);
            await Check.ThrowsAsync<RobloxRateLimitedException>(() => worker.PollAsync(default));
            Check.That(watch.NextCheck == deadline.UtcDateTime && watch.Keys.Count == 2
                && watch.Completions.Single() == (2, false), "El lote perdió identificadores, se confirmó o añadió un minuto al reintento");
        });
        suite.AsyncTest("Los conocidos reutilizan datos recientes sin solicitudes ni fechas inventadas", async () =>
        {
            var observed = DateTime.UtcNow.AddSeconds(-40);
            var session = new RobloxCatalogSession(TimeProvider.System);
            session.Store(new(1, "Limited", null, ItemMarketStatus.Limited) { ObservedAtUtc = observed, Details = new() { CreatorId = 1, CreatorType = "User" } });
            var watch = new Watch([new(1)]); var client = new Details();
            using var provider = Provider(watch, client, new MemoryRepository(new Item(1, "Existing")), session: session);
            using var worker = Worker(provider);
            await worker.PollAsync(default);
            Check.That(client.Batches == 0 && watch.ConfirmedAt == observed && watch.NextCheck == observed.AddMinutes(1),
                "Se repitió la consulta o se extendió la vigencia desde el momento de reutilizar");
        });
        suite.AsyncTest("Los conocidos consultan datos caducados y mantienen separados artículos y paquetes", async () =>
        {
            var session = new RobloxCatalogSession(TimeProvider.System);
            session.Store(new(1, "Old", null, ItemMarketStatus.Limited) { ObservedAtUtc = DateTime.UtcNow.AddMinutes(-2), Details = new() { CreatorId = 1, CreatorType = "User" } });
            session.Store(new(2, "Asset", null, ItemMarketStatus.Limited) { ObservedAtUtc = DateTime.UtcNow, Details = new() { CreatorId = 1, CreatorType = "User" } });
            var watch = new Watch([new(1), new(2, CatalogItemType.Bundle)]); var client = new Details();
            using var provider = Provider(watch, client, new MemoryRepository(new Item(999, "Existing")), new Writer(), session: session);
            using var worker = Worker(provider);
            await worker.PollAsync(default);
            Check.That(client.Batches == 1 && client.LastCount == 2, "Se omitieron datos caducados o se mezclaron identidades");
        });
        suite.AsyncTest("Un lote mixto consulta solo los conocidos sin observación reciente", async () =>
        {
            var observed = DateTime.UtcNow.AddSeconds(-40);
            var session = new RobloxCatalogSession(TimeProvider.System);
            session.Store(new(1, "Limited", null, ItemMarketStatus.Limited) { ObservedAtUtc = observed, Details = new() { CreatorId = 1, CreatorType = "User" } });
            var watch = new Watch([new(1), new(2)]); var client = new Details();
            var writer = new Writer();
            using var provider = Provider(watch, client, new MemoryRepository(new Item(999, "Existing")), writer, session: session);
            using var worker = Worker(provider);
            await worker.PollAsync(default);
            Check.That(client.Batches == 1 && client.LastCount == 1 && writer.Count == 2 && watch.Completions.Sum(x => x.Count) == 2
                && watch.Confirmed[new(1)] == observed && watch.Confirmed[new(2)] > observed.AddSeconds(30),
                "Se repitieron consultas o la fecha antigua del dato reutilizado se asigno al dato nuevo");
        });
        suite.AsyncTest("Los conocidos rellenan el lote con cien pendientes reales", async () =>
        {
            var session = new RobloxCatalogSession(TimeProvider.System);
            for (var id = 1; id <= 60; id++) session.Store(new(id, "Limited", null, ItemMarketStatus.Limited)
                { ObservedAtUtc = DateTime.UtcNow.AddSeconds(-20), Details = new() { CreatorId = 1, CreatorType = "User" } });
            var watch = new Watch(Enumerable.Range(1, 160).Select(id => new CatalogItemKey(id)).ToArray());
            var client = new Details(); var writer = new Writer();
            using var provider = Provider(watch, client, new MemoryRepository(new Item(999, "Existing")), writer, session: session);
            using var worker = Worker(provider);
            var count = await worker.PollAsync(default);
            Check.That(count == 160 && client.Batches == 1 && client.LastCount == 100 && watch.Confirmed.Count == 160 && writer.Sizes.SequenceEqual(new[] { 120, 40 }),
                "El lote no se completo con pendientes o se perdieron las observaciones reutilizadas");
        });
        suite.AsyncTest("Known limited watch restores normal state without thumbnails", async () =>
        {
            var item = new Item(1, "Existing", null, ItemMarketStatus.Normal);
            var repo = new MemoryRepository(item);
            var watch = new Watch([new(1)]);
            var client = new Details();
            using var provider = Provider(watch, client, repo);
            using var worker = Worker(provider);
            Check.That(await worker.PollAsync(default) == 1 && item.MarketStatus == ItemMarketStatus.Limited && item.DomainEvents.Count == 1,
                "Known ID was dropped because its local status was normal");
            Check.That(client.Batches == 1 && repo.Saves == 1 && watch.Completions.Single().Success && watch.Keys.Contains(new(1)),
                "Watch was removed, duplicated or not confirmed");
        });
        suite.AsyncTest("Known limited confirmation saves one batch of 100", async () =>
        {
            var watch = new Watch(Enumerable.Range(1, 100).Select(x => new CatalogItemKey(x)).ToArray());
            var client = new Details();
            var writer = new Writer();
            using var provider = Provider(watch, client, new MemoryRepository(new Item(999, "Other")), writer);
            using var worker = Worker(provider);
            Check.That(await worker.PollAsync(default) == 100 && client.Batches == 1 && client.LastCount == 100 && writer.Calls == 1 && writer.Count == 100,
                "Known IDs were fetched or written individually");
            Check.That(watch.Completions.Single(x => x.Count > 0).Success && watch.Completions.Single(x => x.Count > 0).Count == 100,
                "Successful batch was not scheduled for review");
        });
        suite.AsyncTest("Known limited request failure retains all leases for retry", async () =>
        {
            var watch = new Watch([new(1), new(2)]);
            var client = new Details { Throw = true };
            using var provider = Provider(watch, client, new MemoryRepository(new Item(1, "One")));
            using var worker = Worker(provider);
            var failed = false;
            try { await worker.PollAsync(default); } catch (HttpRequestException) { failed = true; }
            Check.That(failed && watch.Completions.Single().Count == 2 && !watch.Completions.Single().Success && watch.Keys.Count == 2,
                "Failed request discarded IDs or marked them confirmed");
        });
        suite.AsyncTest("Known limited omission records retry without confirming missing ID", async () =>
        {
            var watch = new Watch([new(1), new(2)]);
            var failures = new MemoryFailureStore();
            using var provider = Provider(watch, new Details { Omit = 2 }, new MemoryRepository(new Item(1, "One")), failures: failures);
            using var worker = Worker(provider);
            await worker.PollAsync(default);
            Check.That(watch.Completions.Any(x => x.Count == 1 && x.Success) && watch.Completions.Any(x => x.Count == 1 && !x.Success)
                && (await failures.GetDueAsync(DateTime.UtcNow, 100, default)).SequenceEqual(new long[] { 2 }), "Omitted ID was lost or falsely confirmed");
        });
        suite.AsyncTest("Empty known watch performs no provider requests", async () =>
        {
            var client = new Details();
            using var provider = Provider(new Watch([]), client, new MemoryRepository(new Item(1, "One")));
            using var worker = Worker(provider);
            Check.That(await worker.PollAsync(default) == 0 && client.Batches == 0, "Empty watch queried provider");
        });
        suite.AsyncTest("Recent windows cap 1200 and 600 and restart at the head", async () =>
        {
            foreach (var limit in new[] { 600, 1200 })
            {
                var calls = new List<string?>();
                var client = new Discovery((p,c) =>
                {
                    Check.That(p.Scope == (limit == 600 ? CatalogSearchScope.Collectibles : CatalogSearchScope.All) && p.SortType == 3, "Window changed scope or ordering");
                    calls.Add(c);
                    var offset = c is null ? 0 : int.Parse(c);
                    return new(Enumerable.Range(offset + 1, 119).Select(x => (long)x).ToArray(), (offset + 119).ToString()) { BundledAssets = [new(90000)] };
                });
                for (var pass = 0; pass < 2; pass++)
                {
                    var keys = new List<CatalogItemKey>();
                    var result = await CatalogRecentWindow.ReadAsync(client, limit == 600 ? CatalogSearchScope.Collectibles : CatalogSearchScope.All, limit,
                        page => { Check.That(page.BundledAssets.Count == 0, "Window imported unrelated old components"); keys.AddRange(page.Items); return Task.CompletedTask; }, default);
                    Check.That(result.HitLimit && result.Observations == limit && keys.Count == limit && keys.First().Id == 1 && keys.Last().Id == limit,
                        "Window skipped newest IDs or exceeded cap");
                }
                Check.That(calls.Count(x => x is null) == 2 && calls.Count == 2 * (int)Math.Ceiling(limit / 119d), "Window resumed historical backlog instead of head");
            }
        });
        suite.AsyncTest("La ventana reciente conserva la pagina al agotarse el presupuesto", async () =>
        {
            var calls = new List<string?>();
            var client = new Discovery((p,c) =>
            {
                calls.Add(c);
                if (calls.Count == 2) throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMilliseconds(-1));
                return new([1], c is null ? "pendiente" : null);
            });
            var applied = 0;
            var result = await CatalogRecentWindow.ReadAsync(client, CatalogSearchScope.All, 1200, _ => { applied++; return Task.CompletedTask; }, default);
            Check.That(calls.SequenceEqual(new string?[] { null, "pendiente", "pendiente" }) && applied == 2 && result.Pages == 2, "Se repitio la cabecera o se conto el intento fallido");
        });
        suite.AsyncTest("Recent window reports provider exhaustion below requested limit", async () =>
        {
            var calls = 0;
            var client = new Discovery((p,c) => { calls++; return new(Enumerable.Range(1, calls == 9 ? 35 : 120).Select(x => (long)x).ToArray(), calls == 9 ? null : calls.ToString()); });
            var result = await CatalogRecentWindow.ReadAsync(client, CatalogSearchScope.All, 1200, _ => Task.CompletedTask, default);
            Check.That(!result.HitLimit && result.Observations == 995 && result.Pages == 9 && calls == 9, "Provider exhaustion was reported as 1200 fetched items");
        });
        suite.AsyncTest("Recent window rejects cyclic cursors", async () =>
        {
            var client = new Discovery((p,c) => new([], "same"));
            var rejected = false;
            try { await CatalogRecentWindow.ReadAsync(client, CatalogSearchScope.All, 1200, _ => Task.CompletedTask, default); }
            catch (InvalidOperationException) { rejected = true; }
            Check.That(rejected, "Cyclic cursor loop was not stopped");
        });
        suite.AsyncTest("Full limited scan exceeds 600 while recent discovery remains bounded", async () =>
        {
            var calls = new List<string?>();
            var discovery = new Discovery((p,c) =>
            {
                Check.That(p.Scope == CatalogSearchScope.Collectibles && p.SortType == 3, "Wrong limited filter");
                calls.Add(c);
                var page = c is null ? 0 : int.Parse(c);
                return new([], page < 7 ? (page + 1).ToString() : null) { ReturnedCount = 120 };
            });
            var store = new Checkpoints();
            var services = new ServiceCollection();
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(store);
            services.AddSingleton<ICatalogPriorityQueue>(new Queue());
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new CatalogWorkerOptions { BoundedRecentDiscovery = true, BoundedLimitedDiscovery = false, RecentLimitedLimit = 600 }),
                Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.PollAsync(default);
            Check.That(calls.SequenceEqual(new string?[] { null,"1","2","3","4","5","6","7" }) && store.States.Values.Single().Cursor is null,
                "Limited scan stopped at 600 or failed to exhaust its cursor");
        });
        suite.AsyncTest("Complementary limited searches resume separate cursors fairly", async () =>
        {
            var config = new RobloxCatalogOptions();
            var partitions = CatalogComplementaryLimitedWorker.Partitions();
            var store = new Checkpoints();
            string Key(CatalogSearchPartition p) => config.ScopeKey + ":limited-complement:v1:" + p.Key;
            store.States[Key(partitions[0])] = new("saved", false);
            var calls = new List<(string Key, string? Cursor)>();
            var attempts = new List<(string Key, string? Cursor)>();
            var client = new Discovery((p,c) =>
            {
                attempts.Add((p.Key,c));
                if (attempts.Count == 3) throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMilliseconds(-1));
                calls.Add((p.Key,c)); return new([], c == "next" ? null : "next");
            });
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogCheckpointStore>(store);
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(client);
            services.AddSingleton<ICatalogPriorityQueue>(new Queue());
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var worker = new CatalogComplementaryLimitedWorker(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), Options.Create(config), NullLogger<CatalogComplementaryLimitedWorker>.Instance);
            await worker.RunPassAsync(default);
            Check.That(attempts[2] == attempts[3] && attempts.Count == calls.Count + 1, "El reintento cambio de particion o reinicio la ronda complementaria");
            Check.That(calls[0].Cursor == "saved" && calls.Take(partitions.Count).Select(x => x.Key).Distinct().Count() == partitions.Count,
                "One partition starved the rest or lost its saved cursor");
            Check.That(calls.Count == partitions.Count * 2 && store.States.Count == partitions.Count && store.States.Values.All(x => x.Cursor is null), "Not all complementary pages were drained");
            Check.That(partitions.All(x => x.Scope == CatalogSearchScope.Collectibles && x.MinPrice is null && x.CategoryFilter is null)
                && new[] { 0,1,2,4,5 }.All(sort => partitions.Any(x => x.AssetTypeId == 8 && x.SortType == sort)), "Audited coverage filters lost or redundant category filters added");
        });
    }

    private static CatalogKnownLimitedWorker Worker(ServiceProvider p) => new(p.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), NullLogger<CatalogKnownLimitedWorker>.Instance);
    private static ServiceProvider Provider(Watch watch, Details client, MemoryRepository repo, ICatalogPageWriter? writer = null, MemoryFailureStore? failures = null, RobloxCatalogSession? session = null)
    {
        var s = new ServiceCollection(); s.AddLogging();
        s.AddSingleton<ICatalogLimitedWatchlist>(watch); s.AddSingleton<IRobloxCatalogClient>(client);
        s.AddSingleton<IItemRepository>(repo); s.AddSingleton<ICatalogImportFailureStore>(failures ?? new MemoryFailureStore());
        s.AddSingleton<IRobloxThumbnailClient>(new NoImages()); s.AddSingleton(new CatalogRefreshPolicy());
        s.AddScoped<ImportRobloxItemService>(); s.AddScoped<CatalogImportProcessor>();
        if(writer is not null) s.AddSingleton(writer);
        if(session is not null) s.AddSingleton(session);
        return s.BuildServiceProvider();
    }
    private sealed class NoImages : IRobloxThumbnailClient
    { public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken ct = default) => throw new Exception("No thumbnails during limited confirmation"); }
    private sealed class Details : IRobloxCatalogClient
    {
        public int Batches, LastCount; public bool Throw; public long? Omit;
        public DateTimeOffset? RateLimitUntil;
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => throw new Exception("Individual details request");
        public Task<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>> GetItemsAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken ct = default)
        {
            Batches++; LastCount = keys.Count;
            if (RateLimitUntil is { } deadline) throw new RobloxRateLimitedException(deadline);
            if(Throw) throw new HttpRequestException("HTTP 429", null, System.Net.HttpStatusCode.TooManyRequests);
            var observed = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>>(keys.Where(k => k.Id != Omit).ToDictionary(k => k,
                k => new RobloxCatalogResult(new(k.Id, "Limited", null, ItemMarketStatus.Limited) { ItemType = k.Type, ObservedAtUtc = observed, Details = new() { CreatorId = 1, CreatorType = "User" } }, null)));
        }
    }
    private sealed class Watch(IReadOnlyList<CatalogItemKey> keys) : ICatalogLimitedWatchlist
    {
        public HashSet<CatalogItemKey> Keys = keys.ToHashSet();
        public List<(int Count, bool Success)> Completions = [];
        public Dictionary<CatalogItemKey, DateTime> Confirmed = [];
        private readonly HashSet<CatalogItemKey> leased = [];
        public DateTime ConfirmedAt, NextCheck;
        public Task TrackAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime now, CancellationToken ct) { Keys.UnionWith(keys); return Task.CompletedTask; }
        public Task SeedKnownAsync(DateTime now, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CatalogLimitedLease>> ClaimAsync(DateTime now, int limit, TimeSpan lease, CancellationToken ct)
        {
            var selected = Keys.Where(k => !leased.Contains(k)).Take(limit).ToArray();
            leased.UnionWith(selected);
            return Task.FromResult<IReadOnlyList<CatalogLimitedLease>>(selected.Select(k => new CatalogLimitedLease(k, now.Add(lease))).ToArray());
        }
        public Task CompleteAsync(IReadOnlyList<CatalogLimitedLease> entries, DateTime now, DateTime next, bool succeeded, CancellationToken ct) { if(entries.Count>0) { Completions.Add((entries.Count,succeeded)); ConfirmedAt=now; NextCheck=next; foreach(var entry in entries) { leased.Remove(entry.Key); if(succeeded) Confirmed[entry.Key]=now; } } return Task.CompletedTask; }
        public Task<CatalogLimitedWatchHealth> GetHealthAsync(DateTime now, CancellationToken ct) => Task.FromResult(new CatalogLimitedWatchHealth(Keys.Count, Keys.Count, 0, null));
    }
    private sealed class Writer : ICatalogPageWriter
    {
        public int Calls,Count;
        public List<int> Sizes = [];
        public HashSet<CatalogItemKey> Saved = [];
        public Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> rows, bool onlyNew, CancellationToken ct)
        {
            if (rows.Count > 120) throw new ArgumentOutOfRangeException(nameof(rows));
            Check.That(!onlyNew, "Known IDs must update existing items");
            Check.That(rows.Keys.All(k => Saved.Add(k)), "Se guardo dos veces un articulo del lote");
            Calls++; Count=rows.Count; Sizes.Add(rows.Count);
            return Task.FromResult(new CatalogPageWriteResult(0,rows.Count,0,[]));
        }
    }
    private sealed class Discovery(Func<CatalogSearchPartition,string?,RobloxCatalogDiscoveryPage> get) : IRobloxCatalogDiscoveryClient
    {
        public Task<RobloxCatalogDiscoveryPage> GetPageAsync(CatalogSearchScope s, bool recent, string? c, CancellationToken ct) => Task.FromResult(get(new(s) { SortType = recent ? 3 : 0 },c));
        public Task<RobloxCatalogDiscoveryPage> GetPartitionPageAsync(CatalogSearchPartition p,string? c,CancellationToken ct) => Task.FromResult(get(p,c));
        public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? c=null,CancellationToken ct=default) => throw new NotSupportedException();
        public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? c=null,CancellationToken ct=default) => throw new NotSupportedException();
    }
    private sealed class Checkpoints : ICatalogCheckpointStore
    {
        public Dictionary<string,CatalogCheckpoint> States=[];
        public Task<CatalogCheckpoint> LoadAsync(string key,CancellationToken ct) => Task.FromResult(States.GetValueOrDefault(key,new(null,false)));
        public Task SaveAsync(string key,CatalogCheckpoint value,CancellationToken ct) { States[key]=value;return Task.CompletedTask; }
    }
    private sealed class Queue : ICatalogPriorityQueue
    {
        public Task EnqueueAsync(IReadOnlyCollection<CatalogItemKey> keys,DateTime now,CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CatalogPriorityEntry>> GetDueAsync(DateTime now,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(CatalogPriorityEntry e,CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogQueueHealth> GetHealthAsync(DateTime now,CancellationToken ct) => throw new NotSupportedException();
    }
}
