using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Liro.RegressionTests;

internal static class StartupHistoryTests
{
    public static void Register(RegressionSuite suite)
    {
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        suite.AsyncTest("Startup_RepeatedStartsPreserveForeignItemAndAllHistory", async () =>
        {
            var schema = "startup_history_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
                await create.ExecuteNonQueryAsync();
            var isolated = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
            try
            {
                var services = new ServiceCollection();
                services.AddDbContext<LiroDbContext>(o => o.UseNpgsql(isolated, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema)));
                services.AddSingleton(new CatalogRefreshPolicy(null, null));
                await using var provider = services.BuildServiceProvider();
                Guid id;
                using (var scope = provider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<LiroDbContext>();
                    await db.Database.OpenConnectionAsync();
                    await using (var ddl = db.Database.GetDbConnection().CreateCommand())
                    {
                        ddl.CommandText = db.Database.GenerateCreateScript();
                        await ddl.ExecuteNonQueryAsync();
                    }
                    await db.Database.ExecuteSqlRawAsync("""CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)""");
                    foreach (var migration in db.Database.GetMigrations())
                        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "__EFMigrationsHistory" VALUES ({migration}, '10.0.12')""");
                    var now = DateTime.UtcNow;
                    var item = new Item(123, "Preserve foreign history");
                    item.ObserveMarket(new ItemMarketData { LowestResalePriceRobux = 150, HasResellers = true }, now, "test");
                    db.Items.Add(item);
                    await db.SaveChangesAsync();
                    id = item.Id;
                    await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Items" SET "CreatorId"=999, "CreatorType"='User' WHERE "Id"={id}""");
                    db.Set<MarketWatch>().Add(new()
                    {
                        ItemId = id,
                        RecentAveragePrice = 120,
                        NextCheckAtUtc = now
                    });
                    db.Set<MarketRapSnapshot>().Add(new()
                    {
                        ItemId = id,
                        Value = 120,
                        ObservedAtUtc = now
                    });
                    db.Set<MarketHistoryPoint>().Add(new()
                    {
                        ItemId = id,
                        Metric = "VolumenVentas",
                        Value = 3,
                        PeriodUtc = now.Date,
                        ObservedAtUtc = now
                    });
                    await db.SaveChangesAsync();
                }
                var startup = new DatabaseInitializationService(provider.GetRequiredService<IServiceScopeFactory>(),
                    new TestEnvironment(), NullLogger<DatabaseInitializationService>.Instance);
                await startup.StartAsync(default);
                await startup.StartAsync(default);
                using var verify = provider.CreateScope();
                var read = verify.ServiceProvider.GetRequiredService<LiroDbContext>();
                Check.That(await read.Items.AnyAsync(x => x.Id == id), "Restart deleted the foreign item");
                Check.That(await read.Set<MarketHistoryPoint>().Where(x => x.ItemId == id).Select(x => x.Value).SingleAsync() == 3, "Restart changed daily history");
                Check.That(await read.Set<MarketRapSnapshot>().Where(x => x.ItemId == id).Select(x => x.Value).SingleAsync() == 120, "Restart changed RAP history");
                Check.That(await read.Set<MarketWatch>().CountAsync(x => x.ItemId == id) == 1
                    && await read.Set<ItemMarketState>().CountAsync(x => x.ItemId == id) == 1
                    && await read.Set<ItemMarketSnapshot>().CountAsync(x => x.ItemId == id) == 1, "Restart removed related market data");
            }
            finally
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
                await drop.ExecuteNonQueryAsync();
            }
        });
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Staging";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
