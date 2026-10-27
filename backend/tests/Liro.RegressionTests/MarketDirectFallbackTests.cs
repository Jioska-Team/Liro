using Liro.Infrastructure.Roblox;
using Liro.MarketWorker;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Liro.RegressionTests;

internal static class MarketDirectFallbackTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("DirectFallback_RequiresAllProxiesUnavailable_AndHonorsProviderPause", () =>
        {
            Check.That(MarketFallbackPolicy.UseDirect(true, 2, 2, false), "No usa respaldo");
            Check.That(MarketFallbackPolicy.UseDirect(true, 0, 0, false), "No cubre pool vacío");
            Check.That(!MarketFallbackPolicy.UseDirect(true, 2, 1, false), "Abandona proxy disponible");
            Check.That(!MarketFallbackPolicy.UseDirect(true, 2, 2, true), "Evade pausa 403/429");
            Check.That(!MarketFallbackPolicy.UseDirect(false, 2, 2, false), "Ignora configuración");
        });
        suite.Test("DirectFallback_DefaultsToOnePerMinute", () =>
        {
            var options = new MarketOptions();
            Check.That(options.DirectFallbackEnabled && options.DirectRequestsPerMinute == 1, "Respaldo no habilitado");
            options.DirectRequestsPerMinute = 12;
            Check.That(options.IsValid(), "Validated twelve-slot share was rejected");
            options.DirectRequestsPerMinute = 13;
            Check.That(!options.IsValid(), "Market share exceeded twelve slots");
            options.DirectRequestsPerMinute = 12;
            options.MaximumCoverageAgeMinutes = 0;
            Check.That(!options.IsValid(), "Zero coverage window accepted");
            options.MaximumCoverageAgeMinutes = 1441;
            Check.That(!options.IsValid(), "Unbounded coverage window accepted");
        });
        suite.Test("DirectFallback_ValidatesShareAgainstTheExistingGlobalBudget", () =>
        {
            var options = new RobloxRequestOptions { BudgetCapacity = 18 };
            Check.That(MarketDirectBudget.IsValidCapacity(options, 12), "Twelve plus six rejected");
            Check.That(!MarketDirectBudget.IsValidCapacity(options, 13), "Thirteenth direct slot accepted");
            options.BudgetCapacity = 7;
            Check.That(MarketDirectBudget.IsValidCapacity(options, 1), "Seven-slot baseline rejected");
            Check.That(!MarketDirectBudget.IsValidCapacity(options, 2), "Catalog six-slot allowance was lost");
            options.BudgetCapacity = 19;
            Check.That(!MarketDirectBudget.IsValidCapacity(options, 12), "Global eighteen cap was increased");
            options.BudgetCapacity = 18;
            options.BudgetWindow = TimeSpan.FromSeconds(30);
            Check.That(!MarketDirectBudget.IsValidCapacity(options, 12), "Budget window was shortened");
        });
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS");
        suite.AsyncTest("DirectFallback_TwelveMarketAndSixCatalogRequestsShareEighteenSlots", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{test-twelve-" + Guid.NewGuid().ToString("N") + "}:budget";
            var db = redis.GetDatabase();
            await db.StringSetAsync(key + ":mode", "completion-v2");
            var settings = new RobloxRequestOptions { BudgetKey = key, BudgetCapacity = 18, MinimumInterval = TimeSpan.FromSeconds(3) };
            var normal = new RedisRobloxRequestBudget(redis, Options.Create(settings));
            var direct = new MarketDirectBudget(redis, settings, 12);
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    // Advance only the isolated test pacing keys; retain all sixty-second counters.
                    await db.KeyDeleteAsync(new RedisKey[] { key + ":next", key + ":market-direct-next" });
                    Check.That(await direct.TryAcquireAsync("market" + i, default) == TimeSpan.Zero, "Expected market share was unavailable");
                    await direct.CompleteAsync("market" + i, default);
                    if (i == 0)
                    {
                        Check.That(await direct.TryAcquireAsync("too-soon", default) > TimeSpan.FromSeconds(4), "Five-second market cadence was bypassed");
                        Check.That(await normal.TryAcquireAsync("catalog-too-soon", default) > TimeSpan.Zero, "Three-second global cadence was bypassed");
                    }
                }
                await db.KeyDeleteAsync(new RedisKey[] { key + ":next", key + ":market-direct-next" });
                Check.That(await direct.TryAcquireAsync("thirteenth", default) > TimeSpan.Zero, "Market used a thirteenth shared slot");
                for (var i = 0; i < 6; i++)
                {
                    await db.KeyDeleteAsync(key + ":next");
                    Check.That(await normal.TryAcquireAsync("catalog" + i, default) == TimeSpan.Zero, "Market consumed catalog allowance");
                    await normal.CompleteAsync("catalog" + i, default);
                }
                Check.That(await db.SortedSetLengthAsync(key) == 18, "Global requests were counted in independent pools");
                Check.That(await db.SortedSetLengthAsync(key + ":market-direct") == 12, "Market requests were lost from its share");
                await db.KeyDeleteAsync(key + ":next");
                Check.That(await normal.TryAcquireAsync("nineteenth", default) > TimeSpan.Zero, "Exceeded shared eighteen-slot limit");
                await direct.PauseAsync(TimeSpan.FromSeconds(30), default);
                Check.That(await normal.GetCooldownAsync(default) > TimeSpan.Zero, "Direct provider pause was not global");
            }
            finally
            {
                await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases", key + ":market-direct", key + ":market-direct-next", key + ":market-demand", key + ":cooldown" });
            }
        });
        suite.AsyncTest("DirectFallback_ReturnsToProxyWhenItsPauseExpires", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var reference = "TEST_" + Guid.NewGuid().ToString("N");
            var proxy = new MarketProxy("http://127.0.0.1:9", reference);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketProxySecrets:" + reference + ":Username"] = "test",
                ["MarketProxySecrets:" + reference + ":Password"] = "test"
            }).Build();
            var transport = new MarketTransport(config, redis, Options.Create(new MarketOptions()));
            var key = "liro:{market}:proxy-account:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))) + ":cooldown";
            try
            {
                Check.That(!await transport.ShouldUseDirectAsync([proxy]), "No prefiere proxy disponible");
                await redis.GetDatabase().StringSetAsync(key, "1", TimeSpan.FromSeconds(10));
                // A live provider pause is allowed to veto fallback; do not modify the real pause in a test.
                if (await redis.GetDatabase().KeyTimeToLiveAsync("liro:{market}:cooldown") > TimeSpan.Zero)
                    Check.That(!await transport.ShouldUseDirectAsync([proxy]), "Elude pausa del proveedor");
                else
                    Check.That(await transport.ShouldUseDirectAsync([proxy]), "No detecta proxy pausado");
                await redis.GetDatabase().KeyDeleteAsync(key);
                Check.That(!await transport.ShouldUseDirectAsync([proxy]), "No vuelve automáticamente al proxy");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(key); }
        });
        suite.AsyncTest("DynamicBudget_MarketGetsOneOf18_AndUnusedCapacityReturns", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{test-dynamic-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var settings = Options.Create(new RobloxRequestOptions { BudgetKey = key, BudgetCapacity = 18, MinimumInterval = TimeSpan.Zero });
            var normal = new RedisRobloxRequestBudget(redis, settings);
            var direct = new MarketDirectBudget(redis, settings.Value, 1);
            var db = redis.GetDatabase();
            async Task Normal(string id)
            {
                TimeSpan wait;
                while ((wait = await normal.TryAcquireAsync(id, default)) > TimeSpan.Zero)
                    await Task.Delay(wait + TimeSpan.FromMilliseconds(2));
                await normal.CompleteAsync(id, default);
            }
            try
            {
                await normal.TryAcquireAsync("running", default);
                Check.That(await direct.TryAcquireAsync("waiting", default) > TimeSpan.Zero, "Interrumpe una consulta en curso");
                Check.That(await db.KeyExistsAsync(key + ":market-demand"), "No publica demanda de mercado");
                await normal.CompleteAsync("running", default);
                for (var i = 0; i < 16; i++)
                    await Normal("n" + i);
                await db.KeyDeleteAsync(key + ":next");
                Check.That(await normal.TryAcquireAsync("steal", default) > TimeSpan.Zero, "Catálogo roba el turno reservado");
                Check.That(await direct.TryAcquireAsync("market", default) == TimeSpan.Zero, "Mercado sigue sin avanzar con 17 plazas ocupadas");
                await direct.CompleteAsync("market", default);
                Check.That(!await db.KeyExistsAsync(key + ":market-demand"), "Reserva permanente tras atender mercado");
                Check.That(await direct.TryAcquireAsync("second", default) > TimeSpan.Zero, "Supera una consulta por minuto");
                Check.That(!await db.KeyExistsAsync(key + ":market-demand"), "Reserva cupo aunque mercado esté en cooldown local");
                Check.That(await normal.TryAcquireAsync("over18", default) > TimeSpan.Zero, "Supera el máximo compartido");
                Check.That(await db.SortedSetLengthAsync(key) == 18, "La reserva perdió contabilidad");
                // Only this isolated test window is cleared: confirm lending all 18 when market is not due.
                await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next" });
                for (var i = 0; i < 18; i++)
                    await Normal("borrow" + i);
                Check.That(await db.SortedSetLengthAsync(key) == 18, "No presta toda la capacidad disponible");
                Check.That(await normal.TryAcquireAsync("overBorrow", default) > TimeSpan.Zero, "Prestar cupo excede el límite");
            }
            finally
            {
                await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases", key + ":market-direct", key + ":market-direct-next", key + ":market-demand" });
            }
        });
        suite.AsyncTest("DynamicBudget_AbandonedDemandExpires_AndConcurrentReservationsStayBounded", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{test-dynamic-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var settings = Options.Create(new RobloxRequestOptions { BudgetKey = key, BudgetCapacity = 18, MinimumInterval = TimeSpan.Zero });
            var normal = new RedisRobloxRequestBudget(redis, settings);
            var direct = new MarketDirectBudget(redis, settings.Value, 1);
            var db = redis.GetDatabase();
            try
            {
                await normal.TryAcquireAsync("pending", default);
                await direct.TryAcquireAsync("abandoned", default);
                var ttl = await db.KeyTimeToLiveAsync(key + ":market-demand");
                Check.That(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromSeconds(5), "Demanda abandonada no vence");
                await Task.Delay(TimeSpan.FromSeconds(5.1));
                Check.That(!await db.KeyExistsAsync(key + ":market-demand"), "Demanda vencida bloquea catálogo");
                await normal.CompleteAsync("pending", default);
                await db.KeyDeleteAsync(key + ":next");
                var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i => (Id: "d" + i, Wait: await direct.TryAcquireAsync("d" + i, default))));
                var admitted = attempts.Where(x => x.Wait == TimeSpan.Zero).ToArray();
                Check.That(admitted.Length == 1, "Dos procesos reservan el mismo turno");
                await direct.CompleteAsync(admitted[0].Id, default);
                Check.That(await direct.TryAcquireAsync("another", default) > TimeSpan.Zero, "Otro cliente reinicia contador directo");
            }
            finally
            {
                await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases", key + ":market-direct", key + ":market-direct-next", key + ":market-demand" });
            }
        });
    }
}
