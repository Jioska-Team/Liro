using Liro.Infrastructure.Roblox;
using Liro.MarketWorker;
using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
namespace Liro.RegressionTests;

internal static class ValkeyLossTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Loss_ConcurrentColdCallersShareOnePause", () => WithDb(async (redis, db, key) =>
        {
            var options = Settings(key);
            var a = new RedisRobloxRequestBudget(redis, Options.Create(options));
            var b = new MarketDirectBudget(redis, options, 1);
            var delays = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? a.TryAcquireAsync("a" + i, default) : b.TryAcquireAsync("b" + i, default)));
            Check.That(delays.All(d => d >= TimeSpan.FromSeconds(55)), "Cold state admitted or failed to share 60 second pause");
            Check.That(await db.SetLengthAsync(key + ":pending") == 0 && await db.SortedSetLengthAsync(key) == 0, "Pause consumed capacity");
            await db.KeyExpireAsync(key + ":cooldown", TimeSpan.FromSeconds(20));
            Check.That(await b.TryAcquireAsync("again", default) < TimeSpan.FromSeconds(25), "Repeated caller extended quarantine");
        }));
        suite.AsyncTest("Loss_NewProcessDetectsMissingState", () => WithDb(async (redis, db, key) =>
        {
            await db.StringSetAsync(key + ":mode", "completion-v2");
            var a = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            Check.That(await a.TryAcquireAsync("before", default) == TimeSpan.Zero, "Fixture failed");
            await a.CompleteAsync("before", default);
            await Clear(db, key);
            var b = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            Check.That(await b.TryAcquireAsync("new-owner", default) >= TimeSpan.FromSeconds(55), "New owner bypasses data loss");
        }));
        suite.AsyncTest("Loss_Observed429SurvivesDataLoss", () => WithDb(async (redis, db, key) =>
        {
            await db.StringSetAsync(key + ":mode", "completion-v2");
            var a = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            var observer = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            await a.PauseAsync(TimeSpan.FromSeconds(120), default);
            Check.That(await observer.GetCooldownAsync(default) > TimeSpan.FromSeconds(110), "Observer did not see 429");
            await Clear(db, key);
            Check.That(await observer.TryAcquireAsync("blocked", default) > TimeSpan.FromSeconds(110), "Known 429 shortened to recovery pause");
            var market = new MarketDirectBudget(redis, Settings(key), 1);
            Check.That(await market.TryAcquireAsync("also-blocked", default) > TimeSpan.FromSeconds(110), "Restored 429 not shared");
        }));
        suite.AsyncTest("Loss_LateCompletionExtendsRecoveryWithoutShortening429", () => WithDb(async (redis, db, key) =>
        {
            var a = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            await a.TryAcquireAsync("cold", default);
            await db.KeyExpireAsync(key + ":cooldown", TimeSpan.FromSeconds(20));
            await a.CompleteAsync("sent-before-loss", default);
            Check.That(await db.KeyTimeToLiveAsync(key + ":cooldown") > TimeSpan.FromSeconds(55), "Late response did not retain full window");
            await a.PauseAsync(TimeSpan.FromSeconds(120), default);
            await a.CompleteAsync("another-late-response", default);
            Check.That(await db.KeyTimeToLiveAsync(key + ":cooldown") > TimeSpan.FromSeconds(110), "Late response shortened 429");
        }));
        suite.AsyncTest("Loss_UsesLongerConfiguredWindow", () => WithDb(async (redis, db, key) =>
        {
            var settings = Settings(key);
            settings.BudgetWindow = TimeSpan.FromSeconds(90);
            var a = new RedisRobloxRequestBudget(redis, Options.Create(settings));
            Check.That(await a.TryAcquireAsync("cold", default) > TimeSpan.FromSeconds(85), "Recovery shorter than configured window");
        }));
        foreach (var permanent in new[] { false, true })
            suite.AsyncTest(permanent ? "Loss_PreservesIndefinite429" : "Loss_PreservesLonger429", () => WithDb(async (redis, db, key) =>
            {
                if (permanent)
                    await db.StringSetAsync(key + ":cooldown", "429");
                else
                    await db.StringSetAsync(key + ":cooldown", "429", TimeSpan.FromSeconds(120));
                var a = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
                Check.That(await a.TryAcquireAsync("blocked", default) > TimeSpan.Zero, "429 bypassed");
                var ttl = await db.KeyTimeToLiveAsync(key + ":cooldown");
                Check.That(permanent ? ttl is null : ttl > TimeSpan.FromSeconds(110), "429 shortened");
            }));
        suite.AsyncTest("Loss_ExpiredPauseResumesWithoutRearmingAndKeepsLimits", () => WithDb(async (redis, db, key) =>
        {
            var options = Settings(key);
            var a = new RedisRobloxRequestBudget(redis, Options.Create(options));
            var b = new MarketDirectBudget(redis, options, 1);
            Check.That(await a.TryAcquireAsync("cold", default) > TimeSpan.Zero, "No quarantine");
            // Only the fixture clock boundary is accelerated; production duration is asserted separately.
            await db.KeyExpireAsync(key + ":cooldown", TimeSpan.FromMilliseconds(1));
            await Task.Delay(30);
            Check.That(await b.TryAcquireAsync("market", default) == TimeSpan.Zero, "Market did not resume");
            await b.CompleteAsync("market", default);
            for (var i = 0; i < 17; i++)
            {
                Check.That(await a.TryAcquireAsync("n" + i, default) == TimeSpan.Zero, "Capacity lost");
                await a.CompleteAsync("n" + i, default);
            }
            Check.That(await a.TryAcquireAsync("over", default) > TimeSpan.Zero && await b.TryAcquireAsync("over-market", default) > TimeSpan.Zero, "18/1 caps exceeded");
        }));
        suite.AsyncTest("Loss_PreviouslyGrantedAdmissionCannotSend", () => WithDb(async (redis, db, key) =>
        {
            await db.StringSetAsync(key + ":mode", "completion-v2");
            var a = new RedisRobloxRequestBudget(redis, Options.Create(Settings(key)));
            await a.TryAcquireAsync("old", default);
            await Clear(db, key);
            var denied = false;
            try
            {
                denied = await a.GetSendAllowanceAsync("old", default) <= TimeSpan.Zero;
            }
            catch (RobloxRateLimitedException) { denied = true; }
            Check.That(denied, "Old lease still authorizes HTTP");
            Check.That(await db.KeyTimeToLiveAsync(key + ":cooldown") > TimeSpan.FromSeconds(55), "Admission failed to quarantine shared state");
        }));
    }
    private static RobloxRequestOptions Settings(string key) => new() { BudgetKey = key, MinimumInterval = TimeSpan.Zero, BudgetCapacity = 18 };
    private static async Task WithDb(Func<IConnectionMultiplexer, IDatabase, string, Task> body)
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS"));
        var db = redis.GetDatabase();
        var key = "liro:{loss-test-" + Guid.NewGuid().ToString("N") + "}:budget";
        try
        {
            await body(redis, db, key);
        }
        finally { await Clear(db, key); }
    }
    private static Task<long> Clear(IDatabase db, string key) => db.KeyDeleteAsync(new RedisKey[] { key, key + ":mode", key + ":cooldown", key + ":pending", key + ":leases", key + ":next", key + ":market-direct", key + ":market-direct-next", key + ":market-demand", key + ":recovery" });
}
