using System.Net;
using System.Net.Http.Headers;
using Liro.Infrastructure.Market;
using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class MarketTrackingTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Catálogo general valida tipos de valores", () =>
        {
            foreach (var (type, value) in new[] { ("Entero", "18"), ("Decimal", "2.5"), ("Booleano", "false"), ("Json", "{}"), ("Texto", "algo") })
                Entry(value, type).Validate();
            foreach (var (type, value) in new[] { ("Entero", "2.5"), ("Booleano", "quizá"), ("Json", "{"), ("Otro", "1") })
                Check.Throws<ArgumentException>(() => Entry(value, type).Validate());
        });
        suite.Test("Proxy exige configuración activa y credenciales separadas", () =>
        {
            var valid = Entry("""{"Address":"http://proxy.example:8080","SecretReference":"PROXY_01","RequestsPerMinute":6}""", "Json");
            var proxy = MarketProxy.Parse(valid);
            Check.That(proxy.RequestsPerMinute == 6, "No conserva el cupo");
            valid.Activo = false;
            Check.Throws<ArgumentException>(() => MarketProxy.Parse(valid));
            foreach (var address in new[] { "http://user:pass@proxy.example:8080", "file:///tmp/proxy", "http://proxy.example/path" })
                Check.Throws<ArgumentException>(() => MarketProxy.Parse(Entry(System.Text.Json.JsonSerializer.Serialize(new MarketProxy(address, "PROXY_01")), "Json")));
            Check.Throws<MarketHttpException>(() => MarketTransport.CreateProxyClient(proxy, new ConfigurationBuilder().Build()));
        });
        suite.Test("Historial conserva métricas y rechaza contradicciones", () =>
        {
            var observation = ResaleObservation.Parse("""{"recentAveragePrice":123.5,"priceDataPoints":[{"date":"2026-10-16T00:00:00Z","value":150},{"date":"2026-10-16T00:00:00Z","value":150}],"volumeDataPoints":[{"date":"2026-10-16T00:00:00Z","value":2}]}""", Now);
            Check.That(observation.Rap == 123.5m && observation.Points.Count == 2 && observation.DailySales() is null, "Confunde RAP, precio o días faltantes");
            Check.Throws<System.Text.Json.JsonException>(() => ResaleObservation.Parse("""{"recentAveragePrice":null,"priceDataPoints":[],"volumeDataPoints":[]}""", Now));
            Check.Throws<System.Text.Json.JsonException>(() => ResaleObservation.Parse("""{"recentAveragePrice":1,"priceDataPoints":[{"date":"2026-10-16T00:00:00Z","value":1},{"date":"2026-10-16T00:00:00Z","value":2}],"volumeDataPoints":[]}""", Now));
            Check.Throws<System.Text.Json.JsonException>(() => ResaleObservation.Parse("""{"recentAveragePrice":-1,"priceDataPoints":[],"volumeDataPoints":[]}""", Now));
        });
        suite.Test("Frecuencia usa cobertura explícita y no confunde desconocido con inactivo", () =>
        {
            var settings = new MarketOptions();
            var observation = new ResaleObservation(10, Enumerable.Range(1, 7).Select(n => new HistoricalPoint("VolumenVentas", Now.Date.AddDays(-n), 100)).ToArray(), Now);
            Check.That(observation.DailySales() == 100 && settings.Schedule(observation.DailySales()).Interval == TimeSpan.FromMinutes(2), "No clasifica ventas sostenidas");
            Check.That(settings.Schedule(null).Activity == "SinDatos" && settings.Schedule(0).Interval == TimeSpan.FromHours(3), "Confunde falta de datos con cero ventas");
            Check.That(settings.Schedule(10).Interval == TimeSpan.FromMinutes(3) && settings.Schedule(1).Interval == TimeSpan.FromMinutes(30)
                && settings.Schedule(0.1m).Interval == TimeSpan.FromHours(2), "Intervalos incorrectos");
            Check.That(!new MarketOptions { RequestsPerMinute = 19 }.IsValid(), "Permite exceder el presupuesto inicial");
        });
        suite.Test("Respuesta 429 conserva esperas indicadas por proveedor", () =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
            Check.That(MarketTransport.RetryDelay(response, Now) == TimeSpan.FromMinutes(12), "Acorta Retry-After");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(Now.AddMinutes(7)));
            Check.That(MarketTransport.RetryDelay(response, Now) == TimeSpan.FromMinutes(7), "No entiende fecha absoluta");
        });
        suite.AsyncTest("Mercado desactivado no accede a base de datos ni a red", async () =>
        {
            using var provider = new ServiceCollection().BuildServiceProvider();
            using var service = new MarketWorkerService(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new MarketOptions()), null!, NullLogger<MarketWorkerService>.Instance);
            await service.StartAsync(default);
            if (service.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(2));
            await service.StopAsync(default);
        });
        suite.Test("Modelo de mercado conserva unicidad e historial separado", () =>
        {
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
            var catalog = db.Model.FindEntityType(typeof(CatalogoGeneral))!;
            Check.That(catalog.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "Tipo", "Clave" })), "Falta unicidad de configuración");
            Check.That(db.Model.FindEntityType(typeof(MarketHistoryPoint))!.FindPrimaryKey()!.Properties.Count == 4, "No deduplica serie por origen, indicador y fecha");
        });
        RegisterDatabase(suite);
    }

    private static CatalogoGeneral Entry(string value, string type) => new() { Tipo = "CONEXION_MERCADO", Clave = "PRUEBA", Valor = value, TipoDato = type, Activo = true };

    private static void RegisterDatabase(RegressionSuite suite)
    {
        var connection = Environment.GetEnvironmentVariable("LIRO_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        suite.AsyncTest("Mercado reserva sin duplicar y guarda historial idempotente en PostgreSQL", async () =>
        {
            await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).Options);
            await db.Database.OpenConnectionAsync();
            // Temporary session tables only; no production data or schema changes.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE "Items" ("Id" uuid PRIMARY KEY, "CreatorId" bigint, "CreatorType" text, "MarketStatus" text, "CollectibleItemId" uuid);
                CREATE TEMP TABLE "MarketWatches" ("ItemId" uuid PRIMARY KEY, "NextCheckAtUtc" timestamptz NOT NULL,
                    "LastAttemptAtUtc" timestamptz, "LastSuccessAtUtc" timestamptz, "LeaseId" uuid, "LeaseUntilUtc" timestamptz,
                    "ConsecutiveFailures" integer NOT NULL, "LastErrorCode" varchar(60), "RecentAveragePrice" numeric,
                    "RapObservedAtUtc" timestamptz, "DailySales" numeric, "Activity" varchar(30) NOT NULL);
                CREATE TEMP TABLE "MarketHistoryPoints" ("ItemId" uuid, "Source" text, "Metric" text, "PeriodUtc" timestamptz,
                    "Value" numeric, "ObservedAtUtc" timestamptz, PRIMARY KEY ("ItemId","Source","Metric","PeriodUtc"));
                CREATE TEMP TABLE "MarketRapSnapshots" ("ItemId" uuid, "ObservedAtUtc" timestamptz, "Value" numeric, PRIMARY KEY ("ItemId","ObservedAtUtc"));
                """);
            var item = Guid.NewGuid(); var collectible = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Items" VALUES ({item},1,'User','Limited',{collectible})""");
            var store = new MarketStore(db);
            Check.That(await store.SeedAsync(default) == 1 && await store.SeedAsync(default) == 0, "Duplica seguimiento");
            var claim = (await store.ClaimAsync(default))!;
            Check.That(claim.ItemId == item && await store.ClaimAsync(default) is null, "Duplica reserva");
            var observation = new ResaleObservation(123m, [new("PrecioMedioVenta", Now.Date, 120), new("VolumenVentas", Now.Date, 3)], DateTime.UtcNow);
            Check.That(await store.SaveAsync(claim, observation, new(), default), "No guardó observación");
            db.ChangeTracker.Clear();
            Check.That(!await store.SaveAsync(claim, observation, new(), default), "Una reserva obsoleta pudo guardar");
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "NextCheckAtUtc"=NOW() WHERE "ItemId"={item}""");
            var second = (await store.ClaimAsync(default))!;
            Check.That(await store.SaveAsync(second, observation with { ObservedAtUtc = DateTime.UtcNow }, new(), default), "No pudo repetir observación");
            db.ChangeTracker.Clear();
            Check.That(await db.Set<MarketHistoryPoint>().CountAsync() == 2 && await db.Set<MarketRapSnapshot>().CountAsync() == 1, "Duplicó historia o RAP sin cambio");
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "NextCheckAtUtc"=NOW() WHERE "ItemId"={item}""");
            var third = (await store.ClaimAsync(default))!;
            await store.ReleaseAsync(third, "HTTP429", TimeSpan.FromMinutes(12), false, default);
            var after = await db.Set<MarketWatch>().AsNoTracking().SingleAsync();
            Check.That(after.RecentAveragePrice == 123 && after.ConsecutiveFailures == 0 && after.NextCheckAtUtc > DateTime.UtcNow.AddMinutes(11), "El límite destruyó datos o ignoró espera");
        });
    }
}
