using System.Data.Common;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Liro.RegressionTests;

internal static class PersistenceOptimizationTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Observaciones iguales conservan frescura sin repetir instantáneas", () =>
        {
            var item = Fixtures.Item();
            var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
            var data = new ItemMarketData { LowestResalePriceRobux = 100, HasResellers = true, FavoriteCount = 3 };
            item.ObserveMarket(data, now, "primera");
            var first = item.PendingMarketSnapshots.Single();
            item.ObserveMarket(data with
            {
            }, now.AddMinutes(1), "segunda");
            Check.That(item.PendingMarketSnapshots.Count == 1 && first.ObservedAtUtc == now && first.Source == "primera", "Repite o altera el historial");
            Check.That(item.MarketState!.ObservedAtUtc == now.AddMinutes(1) && item.MarketState.Source == "segunda", "Pierde frescura o procedencia");
            item.ObserveMarket(data with
            {
                HasResellers = false
            }, now.AddMinutes(2), "segunda");
            item.ObserveMarket(data with
            {
                HasResellers = false,
                LowestResalePriceRobux = null
            }, now.AddMinutes(3), "segunda");
            item.ObserveMarket(data with
            {
                HasResellers = false,
                LowestResalePriceRobux = null,
                FavoriteCount = 0
            }, now.AddMinutes(4), "segunda");
            Check.That(item.PendingMarketSnapshots.Count == 4 && first.Data.LowestResalePriceRobux == 100,
                "Omite cambios de disponibilidad, ausencia o contador, o destruye la primera observación");
        });
        suite.AsyncTest("Ficha conjunta distingue artículo inexistente y mercado desconocido", async () =>
        {
            var repo = new SummaryStub();
            var controller = new ItemMarketSummaryController(repo);
            Check.That(await controller.Get(Guid.NewGuid(), default) is NotFoundResult, "No devuelve 404");
            repo.Result = new()
            {
                ItemId = Guid.NewGuid(),
                Name = "Ejemplo",
                RobloxItemId = 1,
                ItemType = "Asset"
            };
            var response = (OkObjectResult)await controller.Get(repo.Result.ItemId, default);
            Check.That(ReferenceEquals(response.Value, repo.Result) && repo.Result.RecentAveragePrice is null && repo.Calls == 2,
                "Inventa datos ausentes o repite consultas");
        });
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        suite.AsyncTest("Persistencia evita repeticiones y lee ficha con una sola consulta PostgreSQL", async () =>
        {
            var probe = new CommandProbe();
            await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).AddInterceptors(probe).Options);
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // A private schema inside a rolled-back transaction: no production rows or schema are changed.
            var schema = "liro_read_test_" + Guid.NewGuid().ToString("N");
            await using (var ddl = db.Database.GetDbConnection().CreateCommand())
            {
                ddl.Transaction = transaction.GetDbTransaction();
                // Identifier is generated exclusively from a fixed prefix and a GUID, never external input.
                ddl.CommandText = $"CREATE SCHEMA {schema}; SET LOCAL search_path TO {schema}; " + db.Database.GenerateCreateScript();
                await ddl.ExecuteNonQueryAsync();
            }
            var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
            var item = new Item(101, "Ejemplo");
            var data = new ItemMarketData { LowestResalePriceRobux = 100, HasResellers = true };
            item.ObserveMarket(data, now, "catalogo");
            db.Items.Add(item);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var repository = new ItemRepository(db);
            var detached = (await repository.GetByIdAsync(item.Id))!;
            detached.ObserveMarket(data with
            {
            }, now.AddMinutes(1), "catalogo");
            await repository.UpdateAsync(detached);
            db.ChangeTracker.Clear();
            Check.That(await db.Set<ItemMarketSnapshot>().CountAsync() == 1, "Inserta instantánea idéntica con entidad desconectada");
            var state = await db.Set<ItemMarketState>().AsNoTracking().SingleAsync();
            Check.That(state.ObservedAtUtc == now.AddMinutes(1), "No guarda la última comprobación sin instantánea nueva");
            var changed = (await repository.GetByIdAsync(item.Id))!;
            changed.ObserveMarket(data with
            {
                LowestResalePriceRobux = 110
            }, now.AddMinutes(2), "catalogo");
            await repository.UpdateAsync(changed);
            db.ChangeTracker.Clear();
            Check.That(await db.Set<ItemMarketSnapshot>().CountAsync() == 2, "Pierde cambio real");
            Check.That((await db.Set<ItemMarketSnapshot>().OrderBy(x => x.ObservedAtUtc).FirstAsync()).Data.LowestResalePriceRobux == 100, "Modifica historia existente");
            var prices = await db.Set<ItemMarketState>().AsNoTracking().SingleAsync();
            db.Set<MarketWatch>().Add(new()
            {
                ItemId = item.Id,
                RecentAveragePrice = 95,
                RapObservedAtUtc = now,
                NextCheckAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var reads = probe.Reads;
            var summary = await new ItemMarketSummaryRepository(db).GetAsync(item.Id, default);
            Check.That(probe.Reads == reads + 1 && summary is { LowestResalePriceRobux: 110, RecentAveragePrice: 95, HasResellers: true }
                && summary.PriceObservedAtUtc == prices.ObservedAtUtc && summary.RapObservedAtUtc == now,
                "Ficha repite consultas, pierde valores o confunde fechas");
            reads = probe.Reads;
            Check.That(await repository.ExistsByIdAsync(item.Id) && probe.Reads == reads + 1 && probe.LastRead.Contains("EXISTS")
                && !probe.LastRead.Contains("JOIN"), "Existencia carga estado y relaciones");
            Check.That(!await repository.ExistsByIdAsync(Guid.NewGuid()), "Existencia incorrecta");
            var noMarket = new Item(102, "Sin mercado");
            db.Items.Add(noMarket);
            await db.SaveChangesAsync();
            var empty = await new ItemMarketSummaryRepository(db).GetAsync(noMarket.Id, default);
            Check.That(empty is not null && empty.RecentAveragePrice is null && empty.PriceObservedAtUtc is null, "LEFT JOIN elimina artículo sin mercado");
            Check.That(await new ItemMarketSummaryRepository(db).GetAsync(Guid.NewGuid(), default) is null, "Inventa ficha inexistente");
            await transaction.RollbackAsync();
        });
    }
    private sealed class SummaryStub : IItemMarketSummaryRepository
    {
        public Task<IReadOnlyDictionary<Guid, decimal?>> GetRapAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, decimal?>>(new Dictionary<Guid, decimal?>());
        public ItemMarketSummary? Result; public int Calls;
        public Task<ItemMarketSummary?> GetAsync(Guid id, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
    internal sealed class CommandProbe : DbCommandInterceptor
    {
        public int Reads; public int HistoryWrites; public string LastRead = "";
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            LastRead = command.CommandText;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"MarketHistoryPoints\""))
                HistoryWrites++;
            return ValueTask.FromResult(result);
        }
    }
}

