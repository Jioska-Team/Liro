using Liro.Api.Contracts;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Migrations;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Liro.RegressionTests;

internal static class MarketHistoryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Marketplace URL migration generates values for existing items", () =>
        {
            var operation = new AddItemMarketplaceUrl().UpOperations.Single() as AddColumnOperation;
            Check.That(operation is { Table: "Items", Name: "MarketplaceUrl", IsStored: true }, "URL is not a stored generated column");
            Check.That(operation!.ComputedColumnSql == "'https://www.roblox.com/catalog/' || \"RobloxAssetId\"::text", "Existing asset URLs will be incorrect");
            using var db = CreateContext();
            var property = db.Model.FindEntityType(typeof(Item))!.FindProperty(nameof(Item.MarketplaceUrl))!;
            Check.That(property.GetComputedColumnSql() == operation.ComputedColumnSql && property.GetIsStored() == true, "EF and migration disagree");
        });
        suite.Test("Market migration adds schema without deleting existing data", () =>
        {
            var migration = new AddItemMarketObservations();
            Check.That(migration.UpOperations.All(x => x is AddColumnOperation or CreateTableOperation or CreateIndexOperation), "Migration contains a destructive or unexpected operation");
            var tables = migration.UpOperations.OfType<CreateTableOperation>().Select(x => x.Name).Order().ToArray();
            Check.That(tables.SequenceEqual(new[] { "ItemMarketSnapshots", "ItemMarketState" }), "Unexpected market tables");
            Check.That(migration.UpOperations.OfType<AddColumnOperation>().All(x => x.Table == "Items"), "Existing unrelated table changed");
        });
        suite.Test("Market schema preserves one current row and unique chronological observations", () =>
        {
            using var db = CreateContext();
            var state = db.Model.FindEntityType(typeof(ItemMarketState))!;
            var snapshot = db.Model.FindEntityType(typeof(ItemMarketSnapshot))!;
            Check.That(state.FindPrimaryKey()!.Properties.Single().Name == "ItemId", "Current state is not one per item");
            Check.That(snapshot.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ItemId", "ObservedAtUtc" })), "History cursor is not unique");
            Check.That(snapshot.GetForeignKeys().Single().DeleteBehavior == DeleteBehavior.Restrict, "History can be silently cascaded away");
            Check.That(db.Model.FindEntityType(typeof(Item))!.FindProperty("Version")!.IsConcurrencyToken, "Aggregate concurrency lost");
        });
        foreach (var limit in new[] { 0, 201 })
        {
            suite.AsyncTest("History rejects invalid limit " + limit, async () =>
            {
                var item = Fixtures.Item();
                var history = new HistoryStub();
                var controller = new ItemMarketHistoryController(new ItemService(new MemoryRepository(item)), history);
                Check.That(await controller.Get(item.Id, CancellationToken.None, limit: limit) is BadRequestObjectResult && history.Calls == 0, "Invalid request reached storage");
                using var db = CreateContext();
                await Check.ThrowsAsync<ArgumentException>(() => new ItemMarketHistoryRepository(db).GetBeforeAsync(item.Id, DateTime.UtcNow, limit, CancellationToken.None));
            });
        }
        suite.AsyncTest("History forwards UTC cursor and exposes price source without entity internals", async () =>
        {
            var item = Fixtures.Item();
            var observed = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84,
                LowestResalePriceRobux = 105,
                IsOffSale = false,
                HasResellers = true
            }, observed, "RobloxCatalogDetailsV1");
            var history = new HistoryStub { Rows = item.PendingMarketSnapshots.ToArray() };
            var controller = new ItemMarketHistoryController(new ItemService(new MemoryRepository(item)), history);
            var cursor = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(-5));
            var result = (OkObjectResult)await controller.Get(item.Id, CancellationToken.None, cursor, 1);
            var response = ((ItemMarketResponse[])result.Value!).Single();
            Check.That(history.ItemId == item.Id && history.BeforeUtc == cursor.UtcDateTime && history.Limit == 1, "Pagination arguments changed");
            Check.That(response.AvailablePriceRobux == 84 && response.Data.LowestResalePriceRobux == 105 && response.Source == "RobloxCatalogDetailsV1" && response.ObservedAtUtc == observed, "History response lost market fields");
            var current = ItemResponse.From(item);
            Check.That(current.Market!.AvailablePriceRobux == response.AvailablePriceRobux && current.Market.ObservedAtUtc == observed, "Current API omitted market");
        });
        suite.AsyncTest("History returns not found for an unknown item", async () =>
        {
            var history = new HistoryStub();
            var controller = new ItemMarketHistoryController(new ItemService(new MemoryRepository(Fixtures.Item())), history);
            Check.That(await controller.Get(Guid.NewGuid(), CancellationToken.None) is NotFoundResult && history.Calls == 0, "Unknown item reached history storage");
        });
        suite.AsyncTest("History returns empty array when no observations exist", async () =>
        {
            var item = Fixtures.Item();
            var controller = new ItemMarketHistoryController(new ItemService(new MemoryRepository(item)), new HistoryStub());
            var result = (OkObjectResult)await controller.Get(item.Id, CancellationToken.None);
            Check.That(((ItemMarketResponse[])result.Value!).Length == 0, "Missing history was fabricated");
        });
    }

    private static LiroDbContext CreateContext()
    {
        var context = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        return context;
    }

    private sealed class HistoryStub : IItemMarketHistoryRepository
    {
        public int Calls
        {
            get; private set;
        }
        public Guid ItemId
        {
            get; private set;
        }
        public DateTime BeforeUtc
        {
            get; private set;
        }
        public int Limit
        {
            get; private set;
        }
        public IReadOnlyList<ItemMarketSnapshot> Rows { get; init; } = [];

        public Task<IReadOnlyList<ItemMarketSnapshot>> GetBeforeAsync(Guid itemId, DateTime beforeUtc, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            ItemId = itemId;
            BeforeUtc = beforeUtc;
            Limit = limit;
            var result = Task.FromResult(Rows);
            return result;
        }
    }
}
