using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class KnownWatchOrderingTests
{
    public static void Register(RegressionSuite suite)
    {
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        suite.AsyncTest("La vuelta prioriza pendientes antiguos y guarda fechas individuales en PostgreSQL", async () =>
        {
            await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).Options);
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Session-local tables shadow production names; rollback removes all test data.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE "CatalogLimitedWatches" (
                  "ItemType" text NOT NULL, "ItemId" bigint NOT NULL, "AddedAtUtc" timestamptz NOT NULL,
                  "NextCheckAtUtc" timestamptz NOT NULL, "LastConfirmedAtUtc" timestamptz,
                  PRIMARY KEY ("ItemType", "ItemId")) ON COMMIT DROP;
                CREATE TEMP TABLE "CatalogImportFailures" (
                  "ItemType" text, "AssetId" bigint, "DeadLetteredAtUtc" timestamptz, "NextAttemptAtUtc" timestamptz) ON COMMIT DROP;
                """);
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CatalogLimitedWatches" VALUES
                ('Asset',1,{now},{now.AddHours(-1)},{now.AddSeconds(-1)}),
                ('Asset',2,{now},{now.AddMinutes(-1)},{now.AddMinutes(-10)}),
                ('Asset',3,{now},{now.AddSeconds(-1)},NULL),
                ('Asset',4,{now},{now.AddMinutes(1)},NULL),
                ('Asset',5,{now},{now.AddMinutes(-1)},NULL);
                INSERT INTO "CatalogImportFailures" VALUES ('Asset',5,{now},{now});
                """);
            var store = new CatalogLimitedWatchlist(db);
            var first = await store.ClaimAsync(now, 1, TimeSpan.FromMinutes(5), default);
            var second = await store.ClaimAsync(now, 1, TimeSpan.FromMinutes(5), default);
            Check.That(first.Single().Key.Id == 3 && second.Single().Key.Id == 2,
                "Una fecha programada antigua adelanto a una confirmacion pendiente o se repitio una reserva");
            var times = new Dictionary<CatalogItemKey, DateTime> { [new(3)] = now.AddSeconds(-10), [new(2)] = now.AddSeconds(-20) };
            await store.CompleteObservedAsync(first.Concat(second).ToArray(), times, TimeSpan.FromMinutes(1), default);
            var rows = await db.Set<CatalogLimitedWatch>().AsNoTracking().ToDictionaryAsync(x => x.ItemId);
            Check.That(rows[3].LastConfirmedAtUtc == times[new(3)] && rows[2].LastConfirmedAtUtc == times[new(2)]
                && rows[2].NextCheckAtUtc == now.AddSeconds(40) && rows[3].NextCheckAtUtc == now.AddSeconds(50), "Se mezclaron las fechas de los articulos");
            await store.CompleteObservedAsync(first, new Dictionary<CatalogItemKey, DateTime> { [new(3)] = now }, TimeSpan.FromMinutes(1), default);
            Check.That((await db.Set<CatalogLimitedWatch>().AsNoTracking().SingleAsync(x => x.ItemId == 3)).LastConfirmedAtUtc == now.AddSeconds(-10), "Una reserva obsoleta sobrescribio una confirmacion");
            Check.That((await store.ClaimAsync(now, 100, TimeSpan.FromMinutes(5), default)).Select(x => x.Key.Id).SequenceEqual(new long[] { 1 }),
                "Se repitieron confirmados recientes o se ignoraron la fecha futura y los errores pendientes");
            await transaction.RollbackAsync();
        });
    }
}
