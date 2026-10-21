using Liro.Domain.Catalog.Models;
using System.Text.Json;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class PersistenceTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var mode in new[] { "empty", "items", "retry" })
        {
            suite.AsyncTest($"Catalog deletion with Npgsql retries enabled mode={mode}", async () =>
            {
                var probe = new DeletionDatabaseProbe { WithItems = mode != "empty", FailOnce = mode == "retry" };
                var options = new DbContextOptionsBuilder<Liro.Infrastructure.Persistence.LiroDbContext>()
                    .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused", npgsql => npgsql.EnableRetryOnFailure(2, TimeSpan.Zero, null))
                    .AddInterceptors(new DeletionConnectionProbe(), new DeletionTransactionProbe(probe), new DeletionCommandProbe(probe)).Options;
                using var db = new Liro.Infrastructure.Persistence.LiroDbContext(options);
                var repository = new Liro.Infrastructure.Persistence.Repositories.ItemRepository(db);
                await repository.DeleteAsync(new CatalogItemKey(1));
                Check.That(probe.Committed && probe.Disposed, "Transaction did not commit and dispose");
                Check.That(probe.Queries == (mode == "retry" ? 2 : 1), "Retry did not restart the whole transaction");
                Check.That(probe.DeleteCalls == (mode == "empty" ? 0 : mode == "retry" ? 6 : 4), "Unexpected delete operation count");
                Check.That(db.Database.CurrentTransaction is null, "Completed deletion retained a transaction");
            });
        }
        suite.Test("Outbox retries are scheduled and eventually quarantined", () =>
        {
            var message = new Liro.Infrastructure.Persistence.Outbox.OutboxMessage();
            var now = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 8; i++)
            {
                message.RecordFailure("permanent failure", now);
            }

            Check.That(message.Attempts == 8, "Failure count is incorrect");
            Check.That(message.DeadLetteredAtUtc == now, "Quarantine timestamp is incorrect");
            Check.That(message.NextAttemptAtUtc == now.AddSeconds(1280), "Retry deadline is incorrect");
        });
        suite.AsyncTest("Retrying a failed save does not duplicate outbox records", async () =>
        {
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Liro.Infrastructure.Persistence.LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").AddInterceptors(new FailSaveInterceptor()).Options;
            using var db = new Liro.Infrastructure.Persistence.LiroDbContext(options);
            var item = Fixtures.Item();
            item.UpdateCatalogData("Item", null, ItemMarketStatus.Limited);
            db.Items.Add(item);
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (IntentionalSaveFailure)
                {
                }
            }

            Check.That(db.OutboxMessages.Local.Count == 1, "Retry duplicated the domain event");
            Check.That(item.DomainEvents.Count == 1, "Failed save cleared the event");
        });
        suite.Test("Synchronous save also captures domain events", () =>
        {
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Liro.Infrastructure.Persistence.LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").AddInterceptors(new FailSaveInterceptor()).Options;
            using var db = new Liro.Infrastructure.Persistence.LiroDbContext(options);
            var item = Fixtures.Item();
            item.UpdateCatalogData("Item", null, ItemMarketStatus.Limited);
            db.Items.Add(item);
            try
            {
                db.SaveChanges();
            }
            catch (IntentionalSaveFailure)
            {
            }

            Check.That(db.OutboxMessages.Local.Count == 1, "Sync save bypassed outbox");
        });
        suite.Test("Outbox legacy payload remains readable", () =>
        {
            var evt = new Liro.Domain.Catalog.Events.ItemBecameLimited(Guid.NewGuid(), 123, DateTime.UtcNow);
            var restored = Liro.Infrastructure.Events.DomainEventSerializer.Deserialize(evt.GetType().AssemblyQualifiedName!, JsonSerializer.Serialize(evt));
            Check.That(
                restored is Liro.Domain.Catalog.Events.ItemBecameLimited actual && actual.RobloxAssetId == 123,
                "Legacy event was lost");
            Check.Throws<InvalidOperationException>(() => Liro.Infrastructure.Events.DomainEventSerializer.Deserialize("System.String, System.Private.CoreLib", "{}"));
        });
        suite.Test("EF model and migration snapshot match", () =>
        {
            using var db = new Liro.Infrastructure.Persistence.LiroDbContext(new DbContextOptionsBuilder<Liro.Infrastructure.Persistence.LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
            Check.That(!db.Database.HasPendingModelChanges(), "Model changes have no migration");
            var item = db.Model.FindEntityType(typeof(Item))!;
            Check.That(item.FindProperty(nameof(Item.Version))!.IsConcurrencyToken, "Item concurrency protection is missing");
        });
    }
}

sealed class DeletionDatabaseProbe
{
    public bool WithItems;
    public bool FailOnce;
    public int Queries;
    public int DeleteCalls;
    public bool Committed;
    public bool Disposed;
}

sealed class DeletionConnectionProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionOpeningAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress());
    }
}

sealed class DeletionTransactionProbe(DeletionDatabaseProbe probe) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionStartingEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbTransaction> result, CancellationToken cancellationToken = default)
    {
        var transaction = new DeletionFakeTransaction(connection, probe);
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbTransaction>.SuppressWithResult(transaction));
    }
}

sealed class DeletionFakeTransaction(System.Data.Common.DbConnection connection, DeletionDatabaseProbe probe) : System.Data.Common.DbTransaction
{
    protected override System.Data.Common.DbConnection DbConnection => connection;
    public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.RepeatableRead;
    public override void Commit()
    {
        probe.Committed = true;
    }
    public override void Rollback()
    {
    }
    protected override void Dispose(bool disposing)
    {
        probe.Disposed = true;
        base.Dispose(disposing);
    }
}

sealed class DeletionCommandProbe(DeletionDatabaseProbe probe) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        probe.Queries++;
        var table = new System.Data.DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("RobloxAssetId", typeof(long));
        table.Columns.Add("ItemType", typeof(string));
        if (probe.WithItems)
            table.Rows.Add(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1L, "Asset");
        System.Data.Common.DbDataReader reader = table.CreateDataReader();
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>.SuppressWithResult(reader));
    }
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        probe.DeleteCalls++;
        if (probe.FailOnce && probe.DeleteCalls == 2)
        {
            throw new Npgsql.NpgsqlException("Simulated transient delete failure", new TimeoutException());
        }
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>.SuppressWithResult(1));
    }
}

