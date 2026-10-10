using System.Text.Json;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class PersistenceTests
{
    public static void Register(RegressionSuite suite)
    {
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
