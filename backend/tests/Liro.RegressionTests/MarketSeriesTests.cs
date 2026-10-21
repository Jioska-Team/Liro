using Liro.Api.Controllers;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Liro.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Liro.RegressionTests;

internal static class MarketSeriesTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("MarketSeries_DailyUtcDeduplicatesAndPreservesMissingAndZero", async () =>
        {
            await using var db = await Open();
            var item = Fixtures.Item();
            var id = item.Id;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MarketHistoryPoints" VALUES
                ({id},'marketplace-sales','PrecioMedioVenta','2026-10-01T00:00Z',100,'2026-10-02T00:00Z'),
                ({id},'marketplace-sales','PrecioMedioVenta','2026-10-01T12:00Z',90,'2026-10-02T00:00Z'),
                ({id},'marketplace-sales','VolumenVentas','2026-10-01T00:00Z',2,'2026-10-02T00:00Z'),
                ({id},'other','VolumenVentas','2026-10-01T00:00Z',99,'2026-10-02T00:00Z'),
                ({id},'marketplace-sales','VolumenVentas','2026-10-02T00:00Z',0,'2026-10-03T00:00Z'),
                ({id},'marketplace-sales','PrecioMedioVenta','2026-10-03T00:00Z',0,'2026-10-04T00:00Z');
                """);
            var repo = new ItemMarketSeriesRepository(db);
            var first = await repo.GetDailyAsync(id, new(2026, 10, 1), new(2026, 10, 4), null, 1, default);
            Check.That(first.Points.Count == 1 && first.Points[0].DateUtc == new DateOnly(2026, 10, 1)
                && first.Points[0].AveragePriceRobux == 90 && first.Points[0].SalesVolume == 2
                && first.NextAfter == new DateOnly(2026, 10, 1), "Daily point duplicated, shifted, summed, or lost cursor");
            var rest = await repo.GetDailyAsync(id, new(2026, 10, 1), new(2026, 10, 4), first.NextAfter, 10, default);
            Check.That(rest.Points.Count == 2 && rest.NextAfter is null
                && rest.Points[0].AveragePriceRobux is null && rest.Points[0].SalesVolume == 0
                && rest.Points[1].AveragePriceRobux == 0 && rest.Points[1].SalesVolume is null, "Missing data fabricated or zero lost");
            Check.That(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"MarketHistoryPoints\"").SingleAsync() == 6, "Read changed history");
            Check.That(db.ChangeTracker.Entries().Count() == 0, "Read tracks entities");
        });
        suite.AsyncTest("MarketSeries_RapCursorPreservesPrecisionAndDateBounds", async () =>
        {
            await using var db = await Open();
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MarketRapSnapshots" VALUES
                ({id},'2026-10-01T00:00:00.000001Z',100),
                ({id},'2026-10-01T00:00:00.000002Z',101),
                ({id},'2026-10-02T00:00Z',102);
                """);
            var repo = new ItemMarketSeriesRepository(db);
            var first = await repo.GetRapAsync(id, new(2026, 10, 1), new(2026, 10, 2), null, 1, default);
            var last = await repo.GetRapAsync(id, new(2026, 10, 1), new(2026, 10, 2), first.NextAfter, 1, default);
            Check.That(first.Points.Single().Rap == 100 && last.Points.Single().Rap == 101
                && first.NextAfter is not null && last.NextAfter is null, "RAP cursor repeats/skips points or includes exclusive end");
            Check.That((await repo.GetRapAsync(Guid.NewGuid(), new(2026, 10, 1), new(2026, 10, 2), null, 5, default)).Points.Count == 0, "Unknown item's RAP leaked");
        });
        suite.AsyncTest("MarketSeries_ControllerValidatesAndReturnsNotFoundOrEmpty", async () =>
        {
            await using var db = await Open();
            var item = Fixtures.Item();
            var controller = new ItemMarketSeriesController(new ItemService(new MemoryRepository(item)), new ItemMarketSeriesRepository(db));
            Check.That(await controller.Daily(item.Id, default, limit: 501) is BadRequestObjectResult, "Unbounded limit accepted");
            Check.That(await controller.Daily(item.Id, default, new(2026, 10, 2), new(2026, 10, 1)) is BadRequestObjectResult, "Reversed range accepted");
            Check.That(await controller.Rap(item.Id, default, new(2020, 1, 1), new(2026, 1, 1)) is BadRequestObjectResult, "Unbounded range accepted");
            Check.That(await controller.Daily(Guid.NewGuid(), default) is NotFoundResult, "Unknown item is not 404");
            var result = (OkObjectResult)await controller.Daily(item.Id, default);
            Check.That(((DailySalesPage)result.Value!).Points.Count == 0, "Empty history fabricated");
        });
        suite.AsyncTest("MarketSeries_HttpContractAndConcurrencyLimit", async () =>
        {
            var item = Fixtures.Item();
            var repository = new BlockingSeries();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            builder.Services.AddControllers().AddApplicationPart(typeof(ItemMarketSeriesController).Assembly);
            builder.Services.AddSingleton(new ItemService(new MemoryRepository(item)));
            builder.Services.AddSingleton<IItemMarketSeriesRepository>(repository);
            await using var app = builder.Build();
            app.UseRateLimiter();
            app.MapControllers();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            var path = $"/api/v1/items/{item.Id}/market-history";
            try
            {
                using var invalid = await client.GetAsync(path + "/daily-sales?from=bad-date");
                Check.That(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid HTTP date not rejected");
                using var missing = await client.GetAsync($"/api/v1/items/{Guid.NewGuid()}/market-history/rap");
                Check.That(missing.StatusCode == HttpStatusCode.NotFound, "HTTP unknown item not 404");
                using var rap = await client.GetAsync(path + "/rap?from=2026-10-01&to=2026-10-02");
                using var json = JsonDocument.Parse(await rap.Content.ReadAsStringAsync());
                Check.That(rap.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("points").GetArrayLength() == 0
                    && json.RootElement.GetProperty("toUtcExclusive").GetString() == "2026-10-02", "HTTP RAP contract changed");
                var first = client.GetAsync(path + "/daily-sales");
                var second = client.GetAsync(path + "/daily-sales");
                await repository.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var rejected = await client.GetAsync(path + "/daily-sales");
                Check.That(rejected.StatusCode == HttpStatusCode.TooManyRequests, "Third concurrent read reached storage");
                repository.Release.TrySetResult();
                using var firstResult = await first;
                using var secondResult = await second;
                Check.That(firstResult.StatusCode == HttpStatusCode.OK && secondResult.StatusCode == HttpStatusCode.OK, "Admitted requests did not finish");
            }
            finally { repository.Release.TrySetResult(); await app.StopAsync(); }
        });
    }

    private static async Task<LiroDbContext> Open()
    {
        var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES")).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            SET TIME ZONE 'America/Guayaquil';
            CREATE TEMP TABLE "MarketHistoryPoints" ("ItemId" uuid, "Source" text, "Metric" text, "PeriodUtc" timestamptz, "Value" numeric, "ObservedAtUtc" timestamptz);
            CREATE TEMP TABLE "MarketRapSnapshots" ("ItemId" uuid, "ObservedAtUtc" timestamptz, "Value" numeric);
            """);
        return db;
    }

    private sealed class BlockingSeries : IItemMarketSeriesRepository
    {
        private int entered;
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DailySalesPage> GetDailyAsync(Guid itemId, DateOnly from, DateOnly to, DateOnly? after, int limit, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref entered) == 2)
                BothEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(from, to, [], null);
        }
        public Task<RapHistoryPage> GetRapAsync(Guid itemId, DateOnly from, DateOnly to, DateTimeOffset? after, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new RapHistoryPage(from, to, [], null));
    }
}
