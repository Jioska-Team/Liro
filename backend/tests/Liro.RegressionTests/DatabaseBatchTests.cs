using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Liro.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Liro.Workers.Catalog;

namespace Liro.RegressionTests;

internal static class DatabaseBatchTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var count in new[] { 120, 121, 240, 241 })
        suite.AsyncTest($"El escritor real guarda {count} observaciones en grupos de hasta 120 sin cliente HTTP", async () =>
        {
            var probes = new List<Probe>();
            var services = new ServiceCollection();
            services.AddScoped<LiroDbContext>(_ => { var probe = new Probe(); probes.Add(probe); return Database(probe); });
            services.AddScoped<ICatalogPageWriter>(sp => Writer(sp.GetRequiredService<LiroDbContext>()));
            using var provider = services.BuildServiceProvider();
            var observations = Enumerable.Range(1, count).ToDictionary(id => new CatalogItemKey(id), id => new RobloxCatalogResult(Remote(id), null));
            var page = new RobloxCatalogDiscoveryPage([], null) { Items = observations.Keys.ToArray(), Details = observations };
            var result = await CatalogSearchPageProcessor.ProcessAsync(provider.GetRequiredService<IServiceScopeFactory>(), page, false, new(), default, fetchFallback: false);
            var expected = Enumerable.Range(0, (count + 119) / 120).Select(i => Math.Min(120, count - i * 120)).ToArray();
            Check.That(result.Added == count && result.Failed == 0 && probes.Select(x => x.Added).SequenceEqual(expected)
                && probes.All(x => x.Saves == 1 && x.Reads == 2 && x.Snapshots == x.Added),
                "Se excedio el limite, se perdieron filas o se repitieron guardados");
        });
        suite.AsyncTest("Database page uses two reads and one save for 120 items with snapshots", async () =>
        {
            var probe = new Probe();
            using var db = Database(probe);
            var data = Enumerable.Range(1, 120).ToDictionary(x => new CatalogItemKey(x, x == 120 ? CatalogItemType.Bundle : CatalogItemType.Asset),
                x => Remote(x, x == 120 ? CatalogItemType.Bundle : CatalogItemType.Asset));
            var result = await Writer(db).WriteAsync(data, false, default);
            Check.That(probe.Reads == 2 && probe.Saves == 1 && probe.Added == 120 && probe.Snapshots == 120 && probe.States == 120,
                "Page performed per-item reads/saves or lost related market rows");
            Check.That(result.Added == 120 && result.Updated == 0 && result.RetryIndividually.Count == 0, "Wrong batch outcome");
        });
        suite.AsyncTest("Database batch saves existing limited transition with outbox atomically", async () =>
        {
            var existing = new Item(1, "Old", null, ItemMarketStatus.Normal);
            var probe = new Probe { Existing = existing };
            using var db = Database(probe);
            var result = await Writer(db).WriteAsync(new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1) }, false, default);
            Check.That(probe.Reads == 2 && probe.Saves == 1 && probe.Updated == 1 && probe.Events == 1 && probe.Snapshots == 1 && probe.States == 1,
                "Existing update, market observation and event were not captured in the same save");
            Check.That(result.Added == 0 && result.Updated == 1 && probe.SavedStatus == ItemMarketStatus.Limited, "Update was counted as insertion or status was lost");
        });
        suite.AsyncTest("Database batch skips existing bootstrap rows without saving", async () =>
        {
            var probe = new Probe { Existing = new Item(1, "Old") };
            using var db = Database(probe);
            var result = await Writer(db).WriteAsync(new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1) }, true, default);
            Check.That(probe.Reads == 2 && probe.Saves == 0 && result.Skipped == 1 && result.Added == 0, "Bootstrap wrote an existing identity");
        });
        suite.AsyncTest("Database batch preserves backoff and quarantined typed failures", async () =>
        {
            var probe = new Probe { Failures = [new() { AssetId = 1, NextAttemptAtUtc = DateTime.UtcNow.AddHours(1) },
                new() { AssetId = 1, ItemType = CatalogItemType.Bundle, DeadLetteredAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddHours(-1) }] };
            using var db = Database(probe);
            var data = new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1), [new(1, CatalogItemType.Bundle)] = Remote(1, CatalogItemType.Bundle), [new(2)] = Remote(2) };
            var result = await Writer(db).WriteAsync(data, false, default);
            Check.That(result.Skipped == 2 && result.Added == 1 && probe.Added == 1 && probe.DeletedFailures == 0, "Blocked identities were saved or retry records deleted");
        });
        suite.AsyncTest("Database batch isolates invalid observations and resolves successful retries in save", async () =>
        {
            var probe = new Probe { Failures = [new() { AssetId = 1, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1) }] };
            using var db = Database(probe);
            var data = new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1), [new(2)] = Remote(2) with { Name = "" } };
            var result = await Writer(db).WriteAsync(data, false, default);
            Check.That(probe.Saves == 1 && probe.Added == 1 && probe.DeletedFailures == 1 && result.RetryIndividually.SequenceEqual(new CatalogItemKey[] { new(2) }),
                "Poison record blocked valid data or successful retry cleanup was separate");
        });
        suite.AsyncTest("Database save conflict requests fresh individual fallback", async () =>
        {
            var probe = new Probe { FailSave = true };
            using var db = Database(probe);
            var error = await Check.ThrowsAsync<CatalogBatchWriteException>(() => Writer(db).WriteAsync(new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1) }, false, default));
            Check.That(probe.Saves == 1 && error.InnerException is DbUpdateConcurrencyException, "Save conflict was swallowed or retried with mutated context");
        });
        suite.AsyncTest("Database outage propagates without per-item retry storm", async () =>
        {
            var probe = new Probe { Outage = true };
            using var db = Database(probe);
            var error = await Check.ThrowsAsync<DbUpdateException>(() => Writer(db).WriteAsync(new Dictionary<CatalogItemKey, RobloxCatalogItem> { [new(1)] = Remote(1) }, false, default));
            Check.That(error.InnerException is TimeoutException && probe.Saves == 1, "Database outage was converted into per-item fallback");
        });
    }
    private static RobloxCatalogItem Remote(long id, CatalogItemType type = CatalogItemType.Asset) => new(id, "Item", null, ItemMarketStatus.Limited)
    {
        ItemType = type, Source = "RobloxCatalogSearchV2", ObservedAtUtc = DateTime.UtcNow,
        Details = new() { CreatorId = 1, CreatorType = "User", Restrictions = ["Limited"] },
        Market = new() { PrimaryPriceRobux = 10 }
    };
    private static LiroDbContext Database(Probe probe) => new(new DbContextOptionsBuilder<LiroDbContext>()
        .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
        .AddInterceptors(new DeletionConnectionProbe(), new Reads(probe), new Saves(probe)).Options);
    private static CatalogPageWriter Writer(LiroDbContext db) => new(db, new(), NullLogger<Liro.Application.Catalog.Services.ImportRobloxItemService>.Instance, NullLogger<CatalogPageWriter>.Instance);
    private sealed class Probe
    {
        public Item? Existing;
        public CatalogImportFailure[] Failures = [];
        public bool FailSave;
        public bool Outage;
        public int Reads, Saves, Added, Updated, Snapshots, States, Events, DeletedFailures;
        public ItemMarketStatus? SavedStatus;
    }
    private sealed class Saves(Probe probe) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = data.Context!.ChangeTracker.Entries().ToArray();
            probe.Saves++;
            probe.Added = entries.Count(x => x.Entity is Item && x.State == EntityState.Added);
            probe.Updated = entries.Count(x => x.Entity is Item && x.State == EntityState.Modified);
            probe.Snapshots = entries.Count(x => x.Entity is ItemMarketSnapshot && x.State == EntityState.Added);
            probe.States = entries.Count(x => x.Entity is ItemMarketState && x.State == EntityState.Added);
            probe.Events = entries.Count(x => x.Entity is OutboxMessage && x.State == EntityState.Added);
            probe.DeletedFailures = entries.Count(x => x.Entity is CatalogImportFailure && x.State == EntityState.Deleted);
            probe.SavedStatus = entries.Select(x => x.Entity).OfType<Item>().FirstOrDefault()?.MarketStatus;
            if (probe.FailSave) throw new DbUpdateConcurrencyException("simulated row conflict");
            if (probe.Outage) throw new DbUpdateException("database unavailable", new TimeoutException());
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(entries.Length));
        }
    }
    private sealed class Reads(Probe probe) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            probe.Reads++;
            var table = new DataTable();
            var projection = command.CommandText.Split("FROM", 2)[0];
            var fields = Regex.Matches(projection, "(\\w+)\\.(?:\"([^\"]+)\"|(xmin))").Select(match => (Alias: match.Groups[1].Value, Column: match.Groups[2].Success ? match.Groups[2].Value : "xmin")).ToArray();
            foreach (var field in fields) table.Columns.Add("c" + table.Columns.Count, typeof(object));
            if (command.CommandText.Contains("FROM \"CatalogImportFailures\""))
            {
                foreach (var failure in probe.Failures) table.Rows.Add(fields.Select(field => Value(failure, field.Column)).ToArray());
            }
            else if (probe.Existing is { } item)
            {
                var root = fields[0].Alias;
                table.Rows.Add(fields.Select(field => field.Alias == root ? Value(item, field.Column) : DBNull.Value).ToArray());
            }
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
        private static object Value(object entity, string column)
        {
            var value = entity.GetType().GetProperty(column == "xmin" ? "Version" : column)?.GetValue(entity);
            return value is Enum enumeration ? enumeration.ToString() : value ?? DBNull.Value;
        }
    }
}
