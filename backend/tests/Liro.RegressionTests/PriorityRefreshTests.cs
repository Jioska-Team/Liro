using Liro.Application.Catalog.Services;
using Liro.Application.Catalog.Repositories;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Infrastructure.Roblox;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Liro.CatalogWorker.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Data.Common;

namespace Liro.RegressionTests;

internal static class PriorityRefreshTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("La búsqueda principal inicia otro recorrido sin esperar un minuto", async () =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var discovery = new TestDiscovery { OnCall = count => { if (count == 7) stop.Cancel(); } };
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(new TestCheckpoints());
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()),
                Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.StartAsync(stop.Token);
            try
            {
                await monitor.ExecuteTask!;
            }
            catch (OperationCanceledException) { }
            Check.That(discovery.Cursors.Count >= 7, "El monitor mantuvo la pausa entre recorridos completos");
        });
        suite.AsyncTest("Fresh thumbnail is reused during catalog refresh", async () =>
        {
            var item = Fixtures.Item();
            item.UpdateThumbnail("https://example.com/image.png");
            var thumbnail = new CountingThumbnail();
            var importer = new ImportRobloxItemService(new CatalogStub(), new MemoryRepository(item), thumbnail,
                NullLogger<ImportRobloxItemService>.Instance, new MemoryFailureStore(), new CatalogRefreshPolicy());
            await importer.ImportAsync(item.RobloxAssetId);
            Check.That(thumbnail.Calls == 0 && item.LastCheckedAt is not null, "Refresh fetched a fresh thumbnail or skipped catalog update");
        });
        suite.AsyncTest("Expired unchanged thumbnail renews check time and missing thumbnail retries later", async () =>
        {
            var item = Fixtures.Item();
            item.UpdateThumbnail("https://example.com/image.png");
            item.MarkThumbnailChecked(DateTime.UtcNow.AddDays(-2));
            var updated = item.ThumbnailUpdatedAt;
            var thumbnail = new CountingThumbnail();
            var importer = new ImportRobloxItemService(new CatalogStub(), new MemoryRepository(item), thumbnail,
                NullLogger<ImportRobloxItemService>.Instance, new MemoryFailureStore(), new CatalogRefreshPolicy());
            await importer.ImportAsync(1);
            await importer.ImportAsync(1);
            Check.That(thumbnail.Calls == 1 && item.ThumbnailUpdatedAt == updated && item.ThumbnailCheckedAtUtc > updated,
                "Unchanged image was fetched again or its modification timestamp changed");
            var missing = Fixtures.Item();
            var now = DateTime.UtcNow;
            missing.MarkThumbnailChecked(now);
            var policy = new CatalogRefreshPolicy();
            Check.That(!policy.ShouldRefreshThumbnail(missing, now.AddMinutes(15).AddTicks(-1))
                && policy.ShouldRefreshThumbnail(missing, now.AddMinutes(15)), "Missing image retry boundary is wrong");
            Check.That(!policy.ShouldRefreshThumbnail(item, item.ThumbnailCheckedAtUtc!.Value.AddDays(1).AddTicks(-1))
                && policy.ShouldRefreshThumbnail(item, item.ThumbnailCheckedAtUtc.Value.AddDays(1)), "Daily image boundary is wrong");
        });
        suite.AsyncTest("Limited search retains only verified typed limited signals", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":[
                {"id":1,"itemType":"Asset","creatorTargetId":1,"creatorType":"User","itemRestrictions":["Limited"]},
                {"id":1,"itemType":"Bundle","creatorTargetId":1,"creatorType":"User","itemRestrictions":["LimitedUnique"]},
                {"id":2,"itemType":"Asset","creatorTargetId":1,"creatorType":"Group","itemRestrictions":["Limited"]},
                {"id":3,"itemType":"Asset","creatorTargetId":2,"creatorType":"User","itemRestrictions":["Limited"]},
                {"id":4,"itemType":"Asset","creatorTargetId":1,"creatorType":"User","itemRestrictions":[]},
                {"id":5,"itemType":"Asset","itemRestrictions":["Limited"]}]}
                """) })) { BaseAddress = new("https://catalog.roblox.com/") };
            var page = await new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions())).GetPageAsync(CatalogSearchScope.Collectibles, true, null, default);
            Check.That(page.Items.Count == 6 && page.LimitedItems.SequenceEqual(new CatalogItemKey[] { new(1), new(1, CatalogItemType.Bundle) }), "Creator verification or typed signal was lost");
        });
        suite.AsyncTest("Limited monitor drains full cursor chain without per-page polling delay", async () =>
        {
            var queue = new TestQueue();
            var discovery = new TestDiscovery();
            var checkpoints = new TestCheckpoints();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(queue);
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()),
                Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.PollAsync(default);
            await monitor.PollAsync(default);
            Check.That(discovery.Cursors.SequenceEqual(new string?[] { null, "tail", "tail2", null, "tail", "tail2" }) && discovery.AllUrgent,
                "Monitor did not drain each full pass before the polling delay");
            Check.That(queue.Entries.Count == 2 && checkpoints.Checkpoint.Cursor is null, "Monitor lost typed candidates or checkpoint");
        });
        suite.AsyncTest("Priority confirms known limited before ordinary refresh without thumbnail requests", async () =>
        {
            var known = Fixtures.Item();
            known.MarkChecked(DateTime.UtcNow, TimeSpan.FromMinutes(30));
            var other = new Item(2, "Ordinary");
            other.UpdateThumbnail("https://example.com/image.png");
            var repo = new TestRepository(known, other);
            var queue = new TestQueue();
            await queue.EnqueueAsync([new(1)], DateTime.UtcNow.AddMinutes(-2), default);
            var catalog = new TestCatalog();
            var images = new CountingThumbnail();
            using var provider = Provider(repo, queue, catalog, images);
            var logger = new CaptureLogger();
            var processed = await CatalogRefreshCycle.RunAsync(provider.GetRequiredService<IServiceScopeFactory>(), new()
            {
                ItemDelay = TimeSpan.Zero
            }, logger, default);
            Check.That(processed == 2 && catalog.Calls.SelectMany(x => x).Select(x => x.Id).SequenceEqual(new long[] { 1, 2 }), "Ordinary item ran before priority or work was lost");
            Check.That(known.MarketStatus == ItemMarketStatus.Limited && known.DomainEvents.Count == 1 && queue.Entries.Count == 0 && images.Calls == 0,
                "Known item was skipped, not confirmed, not dequeued, or fetched images");
            Check.That(logger.Messages.Any(x => x.Contains("DetectionToSaveSeconds")), "Detection latency missing");
        });
        suite.AsyncTest("Historical refresh disabled preserves priority confirmations", async () =>
        {
            var known = Fixtures.Item();
            var ordinary = new Item(2, "Ordinary");
            var queue = new TestQueue();
            await queue.EnqueueAsync([new(1)], DateTime.UtcNow, default);
            var catalog = new TestCatalog();
            var images = new CountingThumbnail();
            using var provider = Provider(new TestRepository(known, ordinary), queue, catalog, images);
            var processed = await CatalogRefreshCycle.RunAsync(provider.GetRequiredService<IServiceScopeFactory>(),
                new()
                {
                    HistoricalRefreshEnabled = false
                }, NullLogger.Instance, default);
            Check.That(processed == 1 && catalog.Calls.Count == 1 && catalog.Calls[0].SequenceEqual(new CatalogItemKey[] { new(1) }), "Historical sweep still requested ordinary IDs");
            Check.That(known.MarketStatus == ItemMarketStatus.Limited && queue.Entries.Count == 0 && ordinary.LastCheckedAt is null && images.Calls == 0,
                "Priority work was dropped or ordinary item was refreshed");
        });
        suite.AsyncTest("Historical refresh disabled with empty priority avoids ordinary dependencies", async () =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            using var provider = services.BuildServiceProvider();
            var processed = await CatalogRefreshCycle.RunAsync(provider.GetRequiredService<IServiceScopeFactory>(),
                new()
                {
                    HistoricalRefreshEnabled = false
                }, NullLogger.Instance, default);
            Check.That(processed == 0, "Disabled historical sweep reported work");
        });
        suite.AsyncTest("Priority failure remains queued and retry backoff prevents immediate retry", async () =>
        {
            var queue = new TestQueue();
            await queue.EnqueueAsync([new(1)], DateTime.UtcNow, default);
            var failures = new TestFailures();
            queue.Failures = failures;
            var catalog = new TestCatalog { Invalid = true };
            using var provider = Provider(new TestRepository(Fixtures.Item()), queue, catalog, new CountingThumbnail(), failures);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            await CatalogRefreshCycle.RunAsync(scopes, new()
            {
                ItemDelay = TimeSpan.Zero
            }, NullLogger.Instance, default);
            await CatalogRefreshCycle.RunAsync(scopes, new()
            {
                ItemDelay = TimeSpan.Zero
            }, NullLogger.Instance, default);
            Check.That(queue.Entries.Count == 1 && failures.Blocked.Contains(new(1)) && catalog.Calls.Count == 1, "Failure lost priority or bypassed retry backoff");
        });
        suite.AsyncTest("Priority rate limit retains work without consuming item retry budget", async () =>
        {
            var queue = new TestQueue();
            await queue.EnqueueAsync([new(1)], DateTime.UtcNow, default);
            var failures = new TestFailures();
            var catalog = new TestCatalog { RateLimited = true };
            using var provider = Provider(new TestRepository(Fixtures.Item()), queue, catalog, new CountingThumbnail(), failures);
            await Check.ThrowsAsync<RobloxRateLimitedException>(() => CatalogRefreshCycle.RunAsync(provider.GetRequiredService<IServiceScopeFactory>(), new(), NullLogger.Instance, default));
            Check.That(queue.Entries.Count == 1 && failures.Blocked.Count == 0 && catalog.Calls.Count == 1, "429 dropped work, consumed retry budget or continued ordinary work");
        });
        suite.AsyncTest("Limited monitor failure retains backlog checkpoint", async () =>
        {
            var discovery = new TestDiscovery { FailTail = true };
            var checkpoints = new TestCheckpoints { Checkpoint = new("tail2", false) };
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await Check.ThrowsAsync<HttpRequestException>(() => monitor.PollAsync(default));
            Check.That(checkpoints.Checkpoint.Cursor == "tail2", "Failed tail discarded persisted backlog");
        });
        suite.AsyncTest("Limited scan commits each page and resumes after a later failure", async () =>
        {
            var discovery = new TestDiscovery { FailAt = "tail2" };
            var checkpoints = new TestCheckpoints();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await Check.ThrowsAsync<HttpRequestException>(() => monitor.PollAsync(default));
            Check.That(checkpoints.Checkpoint.Cursor == "tail2", "Successful page was not checkpointed before later failure");
            discovery.FailAt = null;
            discovery.Cursors.Clear();
            await monitor.PollAsync(default);
            Check.That(discovery.Cursors.SequenceEqual(new string?[] { null, "tail2" }) && checkpoints.Checkpoint.Cursor is null, "Restart replayed the entire scan or lost the remaining page");
        });
        suite.AsyncTest("El recorrido reintenta la pagina pendiente sin repetir la cabecera", async () =>
        {
            var discovery = new TestDiscovery();
            discovery.OnCall = count => { if (count is 2 or 3) throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMilliseconds(-1)); };
            var checkpoints = new TestCheckpoints();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.PollAsync(default);
            Check.That(discovery.Cursors.SequenceEqual(new string?[] { null, "tail", "tail", "tail", "tail2" }) && checkpoints.Checkpoint.Cursor is null && discovery.AllUrgent,
                "El 429 reinicio la cabecera, perdio la pagina pendiente o la prioridad");
        });
        suite.AsyncTest("Cancelar la espera por limite conserva el cursor pendiente", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var discovery = new TestDiscovery();
            discovery.OnCall = count => { if (count == 2) { cancellation.Cancel(); throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMinutes(1)); } };
            var checkpoints = new TestCheckpoints();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(checkpoints);
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions()), Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await Check.ThrowsAsync<OperationCanceledException>(() => monitor.PollAsync(cancellation.Token));
            Check.That(checkpoints.Checkpoint.Cursor == "tail" && discovery.Cursors.Count == 2, "La cancelacion perdio el cursor o envio otra consulta");
        });
        suite.AsyncTest("Limited scan revisits head without replacing backlog cursor", async () =>
        {
            var discovery = new TestDiscovery();
            var services = new ServiceCollection();
            services.AddSingleton<ICatalogPriorityQueue>(new TestQueue());
            services.AddSingleton<IRobloxCatalogDiscoveryClient>(discovery);
            services.AddSingleton<ICatalogCheckpointStore>(new TestCheckpoints());
            using var provider = services.BuildServiceProvider();
            using var monitor = new CatalogLimitedMonitor(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CatalogWorkerOptions { LimitedPollInterval = TimeSpan.Zero }), Options.Create(new RobloxCatalogOptions()), NullLogger<CatalogLimitedMonitor>.Instance);
            await monitor.PollAsync(default);
            Check.That(discovery.Cursors.SequenceEqual(new string?[] { null, null, "tail", null, "tail2" }), "Head checks skipped or replaced the advancing cursor");
        });
        suite.Test("Priority migration adds queue and thumbnail timestamp without rewriting items", () =>
        {
            var migration = new Liro.Infrastructure.Persistence.Migrations.AddCatalogPriorityRefresh();
            Check.That(migration.UpOperations.Count == 3
                && migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation>().Single().Name == "CatalogPriorityRefreshes"
                && migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>().Single().Name == "ThumbnailCheckedAtUtc"
                && !migration.UpOperations.Any(x => x is Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation or Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation), "Migration rewrites or removes existing catalog data");
        });
        suite.AsyncTest("Priority imports unknown bundle and keeps asset identity separate", async () =>
        {
            var repo = new TestRepository(Fixtures.Item());
            var queue = new TestQueue();
            await queue.EnqueueAsync([new(1, CatalogItemType.Bundle)], DateTime.UtcNow, default);
            using var provider = Provider(repo, queue, new TestCatalog(), new CountingThumbnail());
            await CatalogRefreshCycle.RunAsync(provider.GetRequiredService<IServiceScopeFactory>(), new()
            {
                ItemDelay = TimeSpan.Zero
            }, NullLogger.Instance, default);
            Check.That(repo.Items.ContainsKey(new(1, CatalogItemType.Bundle)) && repo.Items.ContainsKey(new(1)) && queue.Entries.Count == 0, "Bundle overwrote asset or was not imported");
        });
        suite.AsyncTest("Refresh drains pending batches without cycle sleep and reports backlog", async () =>
        {
            var items = Enumerable.Range(1, 21).Select(id => { var item = new Item(id, "Item"); item.UpdateThumbnail("https://example.com/image.png"); return item; }).ToArray();
            var repo = new TestRepository(items);
            var logger = new CaptureLogger();
            using var provider = Provider(repo, new TestQueue(), new TestCatalog(), new CountingThumbnail());
            using var worker = new CatalogRefreshWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger,
                Options.Create(new CatalogWorkerOptions { ItemDelay = TimeSpan.Zero, CycleInterval = TimeSpan.FromHours(1), RefreshIdleDelay = TimeSpan.FromHours(1) }));
            await worker.StartAsync(default);
            try
            {
                await repo.AllSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { await worker.StopAsync(default); }
            Check.That(items.All(x => x.LastCheckedAt is not null) && logger.Messages.Any(x => x.Contains("PriorityPending")), "Pending batches slept or health was not emitted");
        });
        suite.AsyncTest("HTTP priority overtakes ordinary traffic but reserves ordinary turns", async () =>
        {
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }), TimeProvider.System, NullLogger<RobloxRequestGate>.Instance);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var first = gate.SendAsync(async _ => { entered.SetResult(); await release.Task; return new(HttpStatusCode.OK); }, default);
            await entered.Task;
            var normal = gate.SendAsync(_ => { order.Add("normal"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            var pending = new List<Task<HttpResponseMessage>> { first, normal };
            using (new RobloxRequestPriority())
            {
                for (var i = 0; i < 5; i++)
                    pending.Add(gate.SendAsync(_ => { order.Add("urgent"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default));
            }
            release.SetResult();
            foreach (var response in await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)))
                response.Dispose();
            Check.That(order.SequenceEqual(new[] { "urgent", "urgent", "urgent", "normal", "urgent", "urgent" }), "Priority or starvation protection failed");
        });
        suite.AsyncTest("Cancelled priority waiter does not block HTTP gate", async () =>
        {
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }), TimeProvider.System, NullLogger<RobloxRequestGate>.Instance);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = gate.SendAsync(async _ => { await release.Task; return new(HttpStatusCode.OK); }, default);
            using var cts = new CancellationTokenSource();
            Task<HttpResponseMessage> cancelled;
            using (new RobloxRequestPriority())
                cancelled = gate.SendAsync(_ => throw new Exception("Cancelled request was sent"), cts.Token);
            cts.Cancel();
            await Check.ThrowsAsync<TaskCanceledException>(async () => await cancelled);
            release.SetResult();
            (await first).Dispose();
            using var next = await gate.SendAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.That(next.IsSuccessStatusCode, "Cancelled waiter retained the permit");
        });
        suite.AsyncTest("Priority persistence deduplicates typed keys atomically and preserves detection time", async () =>
        {
            var probe = new QueueCommandProbe();
            var options = new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
                .AddInterceptors(new DeletionConnectionProbe(), probe).Options;
            using var db = new LiroDbContext(options);
            var store = new CatalogPriorityQueue(db);
            var now = DateTime.UtcNow;
            await store.EnqueueAsync([new(1), new(1), new(1, CatalogItemType.Bundle)], now, default);
            Check.That(probe.Commands.Count == 2 && probe.Commands.All(x => x.Contains("ON CONFLICT") && x.Contains("DO NOTHING") && x.Contains("LimitedUnique")),
                "Enqueue lost atomic deduplication or already-limited filter");
            Check.That(probe.Values.Any(x => Equals(x, "Asset")) && probe.Values.Any(x => Equals(x, "Bundle")) && probe.Values.Any(x => Equals(x, now)), "Typed identity or first detection timestamp not persisted");
            await store.CompleteAsync(new(new(1, CatalogItemType.Bundle), now), default);
            Check.That(probe.Commands.Last().Contains("DetectedAtUtc") && probe.Commands.Last().Contains("ItemType"), "Acknowledgement can delete a newer or differently typed request");
            await store.GetDueAsync(now, 10, default);
            Check.That(probe.Commands.Last().Contains("DeadLetteredAtUtc") && probe.Commands.Last().Contains("NextAttemptAtUtc")
                && probe.Commands.Last().Contains("ORDER BY"), "Priority query ignores backoff or oldest-first order");
        });
    }

    private sealed class CountingThumbnail : IRobloxThumbnailClient
    {
        public int Calls;
        public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>("https://example.com/image.png");
        }
    }

    private sealed class TestQueue : ICatalogPriorityQueue
    {
        public Dictionary<CatalogItemKey, CatalogPriorityEntry> Entries { get; } = [];
        public TestFailures? Failures;
        public Task EnqueueAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime now, CancellationToken ct)
        {
            foreach (var key in keys)
                Entries.TryAdd(key, new(key, now));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CatalogPriorityEntry>> GetDueAsync(DateTime now, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogPriorityEntry>>(Entries.Values.Where(x => Failures?.Blocked.Contains(x.Key) != true).Take(limit).ToArray());
        public Task CompleteAsync(CatalogPriorityEntry entry, CancellationToken ct)
        {
            Entries.Remove(entry.Key);
            return Task.CompletedTask;
        }
        public Task<CatalogQueueHealth> GetHealthAsync(DateTime now, CancellationToken ct) => Task.FromResult(new CatalogQueueHealth(Entries.Count, Entries.Values.Select(x => (DateTime?)x.DetectedAtUtc).Min(), 1, now.AddHours(-1)));
    }
    private sealed class TestRepository(params Item[] items) : IItemRepository
    {
        public Dictionary<CatalogItemKey, Item> Items { get; } = items.ToDictionary(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType));
        public TaskCompletionSource AllSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(key));
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.Values.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken ct = default) => Task.FromResult(Items.ContainsKey(key));
        public Task AddAsync(Item item, CancellationToken ct = default) => UpdateAsync(item, ct);
        public Task UpdateAsync(Item item, CancellationToken ct = default)
        {
            Items[new(item.RobloxAssetId, item.ItemType)] = item;
            if (Items.Values.All(x => x.LastCheckedAt is not null))
                AllSaved.TrySetResult();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CatalogItemKey key, CancellationToken ct = default)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeferRefreshAsync(CatalogItemKey key, DateTime time, CancellationToken ct = default)
        {
            Items[key].MarkChecked(time, TimeSpan.FromMinutes(30));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime now, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogItemKey>>(Items.Where(x => x.Value.NextRefreshAt <= now).Take(limit).Select(x => x.Key).ToArray());
    }
    private sealed class TestCatalog : IRobloxCatalogClient
    {
        public bool Invalid;
        public bool RateLimited;
        public List<CatalogItemKey[]> Calls { get; } = [];
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>> GetItemsAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken ct = default)
        {
            Calls.Add(keys.ToArray());
            if (RateLimited)
                throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMinutes(1));
            return Task.FromResult<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>>(keys.ToDictionary(x => x, x => Invalid ? new RobloxCatalogResult(null, "invalid") : new(new(x.Id, "Confirmed", null, ItemMarketStatus.Limited)
            {
                ItemType = x.Type,
                Details = new()
                {
                    CreatorId = 1,
                    CreatorType = "User"
                }
            }, null)));
        }
    }
    private sealed class TestFailures : ICatalogImportFailureStore
    {
        public HashSet<CatalogItemKey> Blocked { get; } = [];
        public Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken ct) => Task.FromResult(!Blocked.Contains(key));
        public Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken ct)
        {
            Blocked.Add(key);
            return Task.CompletedTask;
        }
        public Task ResolveAsync(CatalogItemKey key, CancellationToken ct)
        {
            Blocked.Remove(key);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogItemKey>>([]);
    }
    private sealed class TestDiscovery : IRobloxCatalogDiscoveryClient
    {
        public bool FailTail;
        public string? FailAt;
        public List<string?> Cursors { get; } = [];
        public bool AllUrgent = true;
        public Action<int>? OnCall;
        public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RobloxCatalogDiscoveryPage> GetPageAsync(CatalogSearchScope scope, bool recent, string? cursor, CancellationToken ct)
        {
            Check.That(scope == CatalogSearchScope.Collectibles && recent, "Wrong search scope");
            Cursors.Add(cursor);
            AllUrgent &= RobloxRequestPriority.IsUrgent;
            OnCall?.Invoke(Cursors.Count);
            if ((FailTail && cursor is not null) || (FailAt is not null && cursor == FailAt))
                throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(new RobloxCatalogDiscoveryPage([], cursor == null ? "tail" : cursor == "tail" ? "tail2" : null) { LimitedItems = [new(1), new(1, CatalogItemType.Bundle)] });
        }
    }
    private sealed class TestCheckpoints : ICatalogCheckpointStore
    {
        public CatalogCheckpoint Checkpoint = new(null, false);
        public Task<CatalogCheckpoint> LoadAsync(string scope, CancellationToken ct) => Task.FromResult(Checkpoint);
        public Task SaveAsync(string scope, CatalogCheckpoint value, CancellationToken ct)
        {
            Checkpoint = value;
            return Task.CompletedTask;
        }
    }
    private sealed class CaptureLogger : ILogger<CatalogRefreshWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private static ServiceProvider Provider(TestRepository repo, TestQueue queue, TestCatalog catalog, CountingThumbnail images, TestFailures? failures = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IItemRepository>(repo);
        services.AddSingleton<ICatalogPriorityQueue>(queue);
        services.AddSingleton<IRobloxCatalogClient>(catalog);
        services.AddSingleton<IRobloxThumbnailClient>(images);
        services.AddSingleton<ICatalogImportFailureStore>(failures ?? new TestFailures());
        services.AddSingleton(new CatalogRefreshPolicy());
        services.AddLogging();
        services.AddScoped<ImportRobloxItemService>();
        services.AddScoped<CatalogImportProcessor>();
        return services.BuildServiceProvider();
    }
    private sealed class QueueCommandProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<object?> Values { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Commands.Add(command.CommandText);
            Values.AddRange(command.Parameters.Cast<DbParameter>().Select(x => x.Value));
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Commands.Add(command.CommandText);
            var table = new System.Data.DataTable();
            table.Columns.Add("ItemId", typeof(long));
            table.Columns.Add("ItemType", typeof(string));
            table.Columns.Add("DetectedAtUtc", typeof(DateTime));
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}



