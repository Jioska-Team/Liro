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
            Check.That(new MarketOptions { RequestsPerMinute = 30 }.IsValid(), "Rechaza la etapa autorizada de 30 solicitudes");
            Check.That(!new MarketOptions { RequestsPerMinute = 61 }.IsValid(), "Permite exceder el presupuesto máximo de mercado");
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
            if (service.ExecuteTask is { } task)
                await task.WaitAsync(TimeSpan.FromSeconds(2));
            await service.StopAsync(default);
        });
        suite.Test("Modelo de mercado conserva unicidad e historial separado", () =>
        {
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
            var catalog = db.Model.FindEntityType(typeof(CatalogoGeneral))!;
            Check.That(catalog.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "Tipo", "Clave" })), "Falta unicidad de configuración");
            Check.That(db.Model.FindEntityType(typeof(MarketHistoryPoint))!.FindPrimaryKey()!.Properties.Count == 4, "No deduplica serie por origen, indicador y fecha");
        });
        suite.Test("MarketSchedule_RefreshesBothRoutesWithCurrentPopulation", () =>
        {
            var options = new MarketOptions { RequestsPerMinute = 30, DirectRequestsPerMinute = 1, MaximumCoverageAgeMinutes = 90 };
            var first = MarketSchedulePlan.BuildRoutes(options,
                [new() { Activity = "Alta", Count = 10 }, new() { Activity = "SinVentasRecientes", Count = 100 }], 30);
            var second = MarketSchedulePlan.BuildRoutes(options,
                [new() { Activity = "Alta", Count = 100 }, new() { Activity = "SinVentasRecientes", Count = 1000 }], 30);
            Check.That(second.Direct.ActiveMinutes > first.Direct.ActiveMinutes
                && second.Direct.InactiveMinutes > first.Direct.InactiveMinutes,
                "Direct route retained a startup population plan");
            Check.That(second.Proxies.ActiveMinutes > first.Proxies.ActiveMinutes
                && second.Direct.ActiveMinutes > second.Proxies.ActiveMinutes,
                "Both routes must use current population and their own actual capacity");
            Check.That(second.Direct.MaximumCoverageAgeMinutes == 90 && second.Proxies.MaximumCoverageAgeMinutes == 90,
                "Adaptive frequencies lost the explicit coverage age policy");
        });
        suite.Test("Frecuencias adaptadas reservan capacidad para todos los grupos", () =>
        {
            var population = new List<MarketPopulation> { new() { Activity="MuyAlta", Count=9 }, new() { Activity="Alta", Count=81 },
                new() { Activity="Media", Count=132 }, new() { Activity="SinDatos", Count=2332 } };
            var plan = MarketSchedulePlan.Build(new()
            {
                RequestsPerMinute = 30
            }, population, 30);
            Check.That(plan.VeryActiveMinutes == 2 && plan.ActiveMinutes == 6, "No ajusta los objetivos al cupo activo");
            var demand = 9d / plan.VeryActiveMinutes + 81d / plan.ActiveMinutes + 132d / plan.NormalMinutes + 2332d / plan.MissingHistoryMinutes;
            Check.That(demand <= 27 && plan.RequestsPerMinute == 30, "La frecuencia exige más capacidad que la disponible");
            var reduced = MarketSchedulePlan.Build(new()
            {
                RequestsPerMinute = 30
            }, population, 6);
            Check.That(9d / reduced.VeryActiveMinutes + 81d / reduced.ActiveMinutes + 132d / reduced.NormalMinutes + 2332d / reduced.MissingHistoryMinutes <= 5.4,
                "No ajusta frecuencias cuando quedan menos proxies");
            var empty = MarketSchedulePlan.Build(new(), [], 0);
            Check.That(empty.VeryActiveMinutes >= 2 && empty.ActiveMinutes >= 3, "Plan vacío inválido");
        });
        suite.Test("Clasificación parcial prueba actividad sin inventar ventas ni promedios", () =>
        {
            foreach (var (sales, expected) in new[] { (69m, "Media"), (70m, "Alta"), (699m, "Alta"), (700m, "MuyAlta") })
            {
                var e = new MarketActivityEvidence { CompletedDays = 6, WeeklySales = sales, HasRecent = true, HasHistory = true };
                Check.That(e.Activity == expected && e.ExactDailySales is null, "Clasificación parcial o promedio incorrecto");
            }
            Check.That(new MarketActivityEvidence { CompletedDays = 1, WeeklySales = 3, HasRecent = true, HasHistory = true }.Activity == "InciertaReciente", "Oculta incertidumbre reciente");
            Check.That(new MarketActivityEvidence { HasHistory = true }.Activity == "SinVentasRecientes", "Confunde historial antiguo con ausencia de historial");
            Check.That(new MarketActivityEvidence().Activity == "SinHistorial", "No distingue ausencia de historial");
            Check.That(new MarketActivityEvidence { CompletedDays = 6, HasRecent = true, HasHistory = true }.Activity == "InciertaReciente", "Inventa inactividad a partir de días faltantes");
            var full = new MarketActivityEvidence { CompletedDays = 7, WeeklySales = 0, HasRecent = true, HasHistory = true };
            Check.That(full.Activity == "Inactiva" && full.ExactDailySales == 0, "Ignora siete ceros explícitos");
        });
        suite.Test("Plan incluye inciertos y mantiene el presupuesto con historial parcial", () =>
        {
            var plan = MarketSchedulePlan.Build(new()
            {
                RequestsPerMinute = 30
            }, [
                new() {Activity="MuyAlta",Count=9},new() {Activity="Alta",Count=84},new() {Activity="Media",Count=380},
                new() {Activity="InciertaReciente",Count=906},new() {Activity="SinHistorial",Count=57},new() {Activity="SinVentasRecientes",Count=1118}
            ], 30);
            var demand = 9d / plan.VeryActiveMinutes + 84d / plan.ActiveMinutes + 380d / plan.NormalMinutes +
                906d / plan.UncertainMinutes + 57d / plan.MissingHistoryMinutes + 1118d / plan.InactiveMinutes;
            Check.That(demand <= 27 && plan.VeryActiveMinutes == 2 && plan.ActiveMinutes == 6, "Excede cupo o desplaza activos");
            Check.That(plan.MissingHistoryMinutes < plan.UncertainMinutes && plan.UncertainMinutes < plan.InactiveMinutes,
                "No prioriza la comprobación incierta frente al historial antiguo");
        });
        RegisterDatabase(suite);
    }

    private static CatalogoGeneral Entry(string value, string type) => new() { Tipo = "CONEXION_MERCADO", Clave = "PRUEBA", Valor = value, TipoDato = type, Activo = true };

    private static void RegisterDatabase(RegressionSuite suite)
    {
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        foreach (var scenario in new[] { "OldQuiet", "NewFirst", "Retry", "BudgetWait", "Lease", "ForeignCreator", "ExpiredRetry" })
            suite.AsyncTest("MarketCoverage_" + scenario, async () =>
            {
                await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).Options);
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TEMP TABLE "Items" ("Id" uuid PRIMARY KEY,"CreatorId" bigint,"CreatorType" text,"MarketStatus" text,"CollectibleItemId" uuid);
                    CREATE TEMP TABLE "MarketWatches" ("ItemId" uuid PRIMARY KEY,"NextCheckAtUtc" timestamptz,"LastAttemptAtUtc" timestamptz,
                        "LastSuccessAtUtc" timestamptz,"LeaseId" uuid,"LeaseUntilUtc" timestamptz,"LastErrorCode" text,"Activity" text);
                    """);
                var hot = Guid.NewGuid();
                var cold = Guid.NewGuid();
                var collectible = Guid.NewGuid();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Items" VALUES ({hot},1,'User','Limited',{Guid.NewGuid()}),({cold},1,'User','LimitedUnique',{collectible});
                    INSERT INTO "MarketWatches" ("ItemId","NextCheckAtUtc","LastSuccessAtUtc","Activity")
                    VALUES ({hot},NOW()-INTERVAL '1 minute',NOW()-INTERVAL '5 minutes','MuyAlta'),
                           ({cold},NOW()+INTERVAL '20 days',NOW()-INTERVAL '2 days','SinVentasRecientes');
                    """);
                if (scenario == "NewFirst")
                    await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "LastSuccessAtUtc"=NULL WHERE "ItemId"={cold}""");
                if (scenario is "Retry" or "ExpiredRetry")
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE "MarketWatches" SET "LastErrorCode"='HTTP429',
                            "NextCheckAtUtc"=NOW()+{(scenario == "Retry" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(-10))}
                        WHERE "ItemId"={cold}
                        """);
                if (scenario is "BudgetWait" or "Lease")
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE "MarketWatches" SET "LeaseUntilUtc"=NOW()+INTERVAL '1 minute',
                            "LeaseId"={(scenario == "Lease" ? (Guid?)Guid.NewGuid() : null)},
                            "LastErrorCode"={(scenario == "BudgetWait" ? "CupoMercado" : (string?)null)} WHERE "ItemId"={cold}
                        """);
                if (scenario == "ForeignCreator")
                    await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Items" SET "CreatorId"=2 WHERE "Id"={cold}""");
                if (scenario is "OldQuiet" or "NewFirst")
                {
                    var otherOld = Guid.NewGuid();
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "Items" VALUES ({otherOld},1,'User','Limited',{Guid.NewGuid()});
                        INSERT INTO "MarketWatches" ("ItemId","NextCheckAtUtc","LastSuccessAtUtc","Activity")
                        VALUES ({otherOld},NOW()-INTERVAL '30 days',NOW()-INTERVAL '1 day','MuyAlta')
                        """);
                }
                var plan = new MarketOptions { InactiveMinutes = 30000 };
                var claim = await new MarketStore(db).ClaimAsync(default, false, plan);
                var expectsCold = scenario is "OldQuiet" or "NewFirst" or "ExpiredRetry";
                Check.That(claim?.ItemId == (expectsCold ? cold : hot),
                    "Coverage priority either starved an old item or bypassed retry/lease/official eligibility");
                if (expectsCold)
                    Check.That(claim!.CollectibleItemId == collectible && claim.LeaseId != Guid.Empty, "Claim identity or reservation corrupted");
            });
        suite.AsyncTest("Mercado reserva sin duplicar y guarda historial idempotente en PostgreSQL", async () =>
        {
            var probe = new PersistenceOptimizationTests.CommandProbe();
            await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connection).AddInterceptors(probe).Options);
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
            var item = Guid.NewGuid();
            var collectible = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Items" VALUES ({item},1,'User','Limited',{collectible})""");
            var store = new MarketStore(db);
            Check.That(await store.SeedAsync(default) == 1 && await store.SeedAsync(default) == 0, "Duplica seguimiento");
            var claim = (await store.ClaimAsync(default))!;
            Check.That(claim.ItemId == item && await store.ClaimAsync(default) is null, "Duplica reserva");
            var originalDue = (await db.Set<MarketWatch>().AsNoTracking().SingleAsync()).NextCheckAtUtc;
            await store.ReleaseAsync(claim, "CupoMercado", TimeSpan.FromMinutes(1), false, default);
            var deferred = await db.Set<MarketWatch>().AsNoTracking().SingleAsync();
            Check.That(deferred.NextCheckAtUtc == originalDue && deferred.LeaseUntilUtc > DateTime.UtcNow && deferred.ConsecutiveFailures == 0,
                "La falta de cupo perdió prioridad o contó como error");
            Check.That(await store.ClaimAsync(default) is null, "Ignora la espera independiente del cupo");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketWatches\" SET \"LeaseUntilUtc\"=NULL");
            claim = (await store.ClaimAsync(default))!;
            var observation = new ResaleObservation(123m, [new("PrecioMedioVenta", Now.Date, 120), new("VolumenVentas", Now.Date, 3)], DateTime.UtcNow);
            var historyWrites = probe.HistoryWrites;
            Check.That(await store.SaveAsync(claim, observation, new(), default), "No guardó observación");
            Check.That(probe.HistoryWrites == historyWrites + 1, "No agrupa precio y volumen en una sola operación");
            db.ChangeTracker.Clear();
            Check.That(!await store.SaveAsync(claim, observation, new(), default), "Una reserva obsoleta pudo guardar");
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "NextCheckAtUtc"=NOW() WHERE "ItemId"={item}""");
            var second = (await store.ClaimAsync(default))!;
            Check.That(await store.SaveAsync(second, observation with
            {
                ObservedAtUtc = DateTime.UtcNow
            }, new(), default), "No pudo repetir observación");
            db.ChangeTracker.Clear();
            Check.That(await db.Set<MarketHistoryPoint>().CountAsync() == 2 && await db.Set<MarketRapSnapshot>().CountAsync() == 1, "Duplicó historia o RAP sin cambio");
            Check.That(!await db.Set<MarketHistoryPoint>().AnyAsync(p => p.ObservedAtUtc > observation.ObservedAtUtc), "Reescribe la fecha histórica sin cambio de valor");
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "NextCheckAtUtc"=NOW() WHERE "ItemId"={item}""");
            var third = (await store.ClaimAsync(default))!;
            await store.ReleaseAsync(third, "HTTP429", TimeSpan.FromMinutes(12), false, default);
            var after = await db.Set<MarketWatch>().AsNoTracking().SingleAsync();
            Check.That(after.RecentAveragePrice == 123 && after.ConsecutiveFailures == 0 && after.NextCheckAtUtc > DateTime.UtcNow.AddMinutes(11), "El límite destruyó datos o ignoró espera");
            var hot = Guid.NewGuid();
            var cold = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Items" VALUES ({hot},1,'User','Limited',{Guid.NewGuid()}),({cold},1,'User','Limited',{Guid.NewGuid()})""");
            await store.SeedAsync(default);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "Activity"='MuyAlta', "LastSuccessAtUtc"=NOW()-INTERVAL '3 minutes', "NextCheckAtUtc"=NOW()+INTERVAL '1 hour' WHERE "ItemId"={hot}""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MarketWatches" SET "LastSuccessAtUtc"=NOW()-INTERVAL '20 minutes', "NextCheckAtUtc"=NOW()-INTERVAL '1 day' WHERE "ItemId"={cold}""");
            var prioritized = (await store.ClaimAsync(default, false, new()))!;
            Check.That(prioritized.ItemId == hot, "El atraso general desplaza al activo o ignora la frecuencia efectiva");
            await store.ReleaseAsync(prioritized, "CupoConexion", TimeSpan.Zero, false, default);
            var background = (await store.ClaimAsync(default, true, new()))!;
            Check.That(background.ItemId == cold, "La cuota de fondo no avanza con activos pendientes");
            var hotAgain = (await store.ClaimAsync(default, false, new()))!;
            Check.That(hotAgain.ItemId == hot, "La espera por cupo perdió el turno activo");
            await store.ReleaseAsync(hotAgain, "CupoConexion", TimeSpan.Zero, false, default);
            await store.ReleaseAsync(background, "CupoConexion", TimeSpan.Zero, false, default);
            var historyDay = DateTime.UtcNow.Date;
            for (var day = 1; day <= 6; day++)
                await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MarketHistoryPoints" VALUES ({hot},'marketplace-sales','VolumenVentas',{historyDay.AddDays(-day)},{(day == 1 ? 93m : 0m)},{DateTime.UtcNow})""");
            var beforeRetry = after.NextCheckAtUtc;
            Check.That(await store.ReclassifyAsync(DateTime.UtcNow, default) > 0, "No reclasifica el historial existente");
            var classified = await db.Set<MarketWatch>().AsNoTracking().SingleAsync(x => x.ItemId == hot);
            Check.That(classified.Activity == "Alta" && classified.DailySales is null, "Pierde las 93 ventas o publica un promedio incompleto");
            Check.That((await db.Set<MarketWatch>().AsNoTracking().SingleAsync(x => x.ItemId == cold)).Activity == "SinHistorial", "No separa el historial ausente");
            Check.That((await db.Set<MarketWatch>().AsNoTracking().SingleAsync(x => x.ItemId == item)).NextCheckAtUtc == beforeRetry, "La reclasificación ignora la espera del proveedor");
            Check.That(await store.ReclassifyAsync(DateTime.UtcNow, default) == 0, "Reescribe datos sin cambios");
            var storedClaim = (await store.ClaimAsync(default, false, new()))!;
            Check.That(storedClaim.ItemId == hot, "No prioriza el activo reclasificado");
            Check.That(await store.SaveAsync(storedClaim, new ResaleObservation(130, [], DateTime.UtcNow), new(), default), "No guarda respuesta parcial");
            db.ChangeTracker.Clear();
            Check.That((await db.Set<MarketWatch>().AsNoTracking().SingleAsync(x => x.ItemId == hot)).Activity == "Alta", "Una respuesta parcial borró la clasificación basada en historial guardado");


        });
    }
}


