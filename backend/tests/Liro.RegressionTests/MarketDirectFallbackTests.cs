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
        suite.Test("DirectFallback_ConfigurationCapsTwoAndPreservesCatalogHeadroom", () =>
        {
            var options = new MarketOptions();
            Check.That(options.DirectFallbackEnabled && options.DirectRequestsPerMinute == 2, "Respaldo no habilitado");
            options.DirectRequestsPerMinute = 3;
            Check.That(!options.IsValid(), "Permite aumentar respaldo sin revisión");
        });
        var connection = Environment.GetEnvironmentVariable("LIRO_TEST_REDIS");
        if (string.IsNullOrEmpty(connection)) return;
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
                else Check.That(await transport.ShouldUseDirectAsync([proxy]), "No detecta proxy pausado");
                await redis.GetDatabase().KeyDeleteAsync(key);
                Check.That(!await transport.ShouldUseDirectAsync([proxy]), "No vuelve automáticamente al proxy");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(key); }
        });
        suite.AsyncTest("DirectBudget_AtomicHeadroom_PendingPriority_AndTwoPerMinute", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{test-direct-" + Guid.NewGuid().ToString("N") + "}:budget";
            var settings = Options.Create(new RobloxRequestOptions { BudgetKey = key, BudgetCapacity = 18, MinimumInterval = TimeSpan.Zero });
            var normal = new RedisRobloxRequestBudget(redis, settings);
            var direct = new MarketDirectBudget(redis, settings.Value, 2);
            var db = redis.GetDatabase();
            try
            {
                await normal.TryAcquireAsync("catalog", default);
                Check.That(await direct.TryAcquireAsync("blocked", default) > TimeSpan.Zero, "Ignora catálogo en curso");
                await normal.CompleteAsync("catalog", default);
                Check.That(await direct.TryAcquireAsync("quiet", default) > TimeSpan.Zero, "No respeta inactividad");
                await db.KeyDeleteAsync(key + ":next");
                await AgeCompletionsAsync(db, key);
                var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i => (Id: "d" + i, Wait: await direct.TryAcquireAsync("d" + i, default))));
                var admitted = attempts.Where(x => x.Wait == TimeSpan.Zero).ToArray();
                Check.That(admitted.Length == 1, "Reserva concurrente no atómica");
                await direct.CompleteAsync(admitted[0].Id, default);
                Check.That(await direct.TryAcquireAsync("spacing", default) > TimeSpan.Zero, "No espacia respaldo");
                await db.KeyDeleteAsync(new RedisKey[] { key + ":next", key + ":market-direct-next" });
                await AgeCompletionsAsync(db, key);
                Check.That(await direct.TryAcquireAsync("second", default) == TimeSpan.Zero, "No permite segundo envío");
                await direct.CompleteAsync("second", default);
                await db.KeyDeleteAsync(new RedisKey[] { key + ":next", key + ":market-direct-next" });
                Check.That(await direct.TryAcquireAsync("third", default) > TimeSpan.Zero, "Supera dos consultas por minuto");
                await db.KeyDeleteAsync(key + ":market-direct");
                // Fill total occupancy to twelve; six slots must remain for normal requests.
                for (var i = 0; i < 9; i++)
                {
                    TimeSpan wait;
                    while ((wait = await normal.TryAcquireAsync("n" + i, default)) > TimeSpan.Zero)
                        await Task.Delay(wait + TimeSpan.FromMilliseconds(2));
                    await normal.CompleteAsync("n" + i, default);
                }
                await db.KeyDeleteAsync(key + ":next");
                await AgeCompletionsAsync(db, key);
                Check.That(await direct.TryAcquireAsync("headroom", default) > TimeSpan.Zero, "Consume margen del catálogo");
                Check.That(await normal.TryAcquireAsync("priority", default) == TimeSpan.Zero, "Bloquea catálogo con cupo disponible");
                await normal.CompleteAsync("priority", default);
            }
            finally
            {
                await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":market-direct", key + ":market-direct-next" });
            }
        });
    }

    private static Task<RedisResult> AgeCompletionsAsync(IDatabase db, string key) => db.ScriptEvaluateAsync("""
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local entries = redis.call('ZRANGE', KEYS[1], 0, -1)
        for _, id in ipairs(entries) do redis.call('ZADD', KEYS[1], now - 10000, id) end
        return #entries
        """, [key]);
}
