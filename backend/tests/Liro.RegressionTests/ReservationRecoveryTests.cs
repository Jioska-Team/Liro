using System.Reflection;
using System.Runtime.ExceptionServices;
using Liro.Infrastructure.Roblox;
using Liro.MarketWorker;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Net;
using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class ReservationRecoveryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Admission_ExpiredReservationNeverSendsAndCompletes", async () =>
        {
            var budget = new ExpiredBudget();
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.FromMilliseconds(1) }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, budget);
            var sent = false;
            try
            {
                using var response = await gate.SendAsync(_ => { sent = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
                throw new Exception("Expired reservation sent");
            }
            catch (RobloxRateLimitedException) { }
            Check.That(!sent && budget.Completed, "Expired request was sent or reservation not completed");
        });
        suite.AsyncTest("Admission_BoundsSendAndPropagatesStop", async () =>
        {
            var budget = new ExpiredBudget { Allowance = TimeSpan.FromMilliseconds(100) };
            using var admission = await RobloxRequestAdmission.CreateAsync(default, (budget, "bounded"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), admission.Token);
                throw new Exception("Send was not bounded");
            }
            catch (OperationCanceledException) { }
            using var stop = new CancellationTokenSource();
            using var linked = await RobloxRequestAdmission.CreateAsync(stop.Token, (budget, "stop"));
            stop.Cancel();
            Check.That(linked.IsCancellationRequested, "Stop did not cancel admitted HTTP work");
        });
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS");
        foreach (var completing in new[] { 0, 1, 2 })
            suite.AsyncTest(completing switch
            {
                1 => "Recovery_LostCompletionKeepsLimits",
                2 => "Recovery_LostCompletionReplyIsIdempotent",
                _ => "Recovery_LostReservationDoesNotBlockMarket"
            }, async () =>
            {
                using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
                var key = "liro:{recovery-" + Guid.NewGuid().ToString("N") + "}:budget";
                // This fixture represents an already initialized budget; cold start has dedicated tests.
                await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
                var options = new RobloxRequestOptions { BudgetKey = key, BudgetCapacity = 18, MinimumInterval = TimeSpan.Zero };
                var db = redis.GetDatabase();
                var fault = new ScriptFault();
                var wrappedDb = ForwardProxy.Wrap<IDatabase>(db, fault.Invoke);
                var wrappedRedis = ForwardProxy.Wrap<IConnectionMultiplexer>(redis,
                    (method, args, next) => method.Name == "GetDatabase" ? wrappedDb : next());
                var direct = new MarketDirectBudget(wrappedRedis, options, 1);
                try
                {
                    await db.StringSetAsync(key + ":mode", "completion-v2");
                    if (completing > 0)
                    {
                        Check.That(await direct.TryAcquireAsync("lost", default) == TimeSpan.Zero, "Cannot reserve first attempt");
                        fault.FailBefore = completing == 1;
                        fault.TargetComplete = true;
                        fault.Armed = true;
                        try
                        {
                            await direct.CompleteAsync("lost", default);
                            throw new Exception("Fault not triggered");
                        }
                        catch (RedisConnectionException) { }
                    }
                    else
                    {
                        fault.Armed = true;
                        try
                        {
                            await direct.TryAcquireAsync("lost", default);
                            throw new Exception("Fault not triggered");
                        }
                        catch (RedisConnectionException) { }
                    }
                    // A later attempt must reconcile the uncertain operation without resetting counters.
                    Check.That(await direct.TryAcquireAsync("later", default) > TimeSpan.Zero, "Lost operation erased the one-per-minute accounting");
                    Check.That(await db.SetLengthAsync(key + ":pending") == 0, "Market left an orphan while its process is alive");
                    Check.That(await db.SortedSetLengthAsync(key) == 1, "Recovery did not conservatively count the attempt");
                    Check.That(await db.SortedSetLengthAsync(key + ":market-direct") == 1, "Recovery lost direct accounting");
                    var normal = new RedisRobloxRequestBudget(redis, Options.Create(options));
                    for (var i = 0; i < 17; i++)
                    {
                        Check.That(await normal.TryAcquireAsync("n" + i, default) == TimeSpan.Zero, "Recovery consumed extra global capacity");
                        await normal.CompleteAsync("n" + i, default);
                    }
                    Check.That(await normal.TryAcquireAsync("over", default) > TimeSpan.Zero, "Exceeded 18 after recovery");
                }
                finally { await Cleanup(db, key); }
            });
        suite.AsyncTest("Recovery_ExpiredRemoteLeaseCountsWindowAndPreservesLiveAndLegacy", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var db = redis.GetDatabase();
            var key = "liro:{recovery-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            try
            {
                await db.StringSetAsync(key + ":mode", "completion-v2");
                // Fixture timestamps use Valkey time, not the workstation clock.
                await db.ScriptEvaluateAsync("""
                    local t=redis.call('TIME')
                    local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
                    redis.call('SADD',KEYS[1],'remote|1|1|expired','remote|2|2|live','remote|3|3|legacy')
                    redis.call('ZADD',KEYS[2],now-1,'remote|1|1|expired',now+300000,'remote|2|2|live')
                    return 1
                    """, [key + ":pending", key + ":leases"]);
                var normal = new RedisRobloxRequestBudget(redis, Options.Create(new RobloxRequestOptions
                {
                    BudgetKey = key,
                    BudgetCapacity = 3,
                    MinimumInterval = TimeSpan.Zero
                }));
                Check.That(await normal.TryAcquireAsync("blocked", default) > TimeSpan.Zero, "Expired reservation was released without counting a full window");
                Check.That(!await db.SetContainsAsync(key + ":pending", "remote|1|1|expired"), "Remote abandoned reservation never recovered");
                Check.That(await db.SortedSetLengthAsync(key) == 1, "Recovered attempt was not counted");
                Check.That(await db.SetLengthAsync(key + ":pending") == 2, "Recovery removed live or legacy reservations");
                var direct = new MarketDirectBudget(redis, new RobloxRequestOptions { BudgetKey = key, MinimumInterval = TimeSpan.Zero }, 1);
                Check.That(await direct.TryAcquireAsync("market", default) > TimeSpan.Zero, "Market bypassed an in-flight reservation");
            }
            finally { await Cleanup(db, key); }
        });
        suite.AsyncTest("Recovery_LeaseAdmissionAndIdempotentCompletionPreserveProxySix", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var db = redis.GetDatabase();
            var key = "liro:{recovery-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var normal = new RedisRobloxRequestBudget(redis, Options.Create(new RobloxRequestOptions
            {
                BudgetKey = key,
                BudgetCapacity = 6,
                MinimumInterval = TimeSpan.Zero
            }));
            try
            {
                Check.That(await normal.TryAcquireAsync("lease", default) == TimeSpan.Zero, "Initial reserve rejected");
                var pending = (await db.SetMembersAsync(key + ":pending")).Single();
                Check.That(await normal.GetSendAllowanceAsync("lease", default) > TimeSpan.FromSeconds(25), "Live lease not admitted");
                await db.ScriptEvaluateAsync("""
                    local t=redis.call('TIME')
                    local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
                    redis.call('ZADD',KEYS[1],now+60000,ARGV[1])
                    return 1
                    """, [key + ":leases"], [pending]);
                Check.That(await normal.GetSendAllowanceAsync("lease", default) <= TimeSpan.Zero, "Admits send inside recovery safety margin");
                await normal.CompleteAsync("lease", default);
                var score = await db.SortedSetScoreAsync(key, pending);
                await normal.CompleteAsync("lease", default);
                Check.That(await db.SortedSetScoreAsync(key, pending) == score, "Duplicate completion extended accounting");
                Check.That(await db.SortedSetLengthAsync(key + ":leases") == 0, "Completed lease remained");
                for (var i = 0; i < 5; i++)
                {
                    Check.That(await normal.TryAcquireAsync("p" + i, default) == TimeSpan.Zero, "Proxy capacity changed");
                    await normal.CompleteAsync("p" + i, default);
                }
                Check.That(await normal.TryAcquireAsync("seventh", default) > TimeSpan.Zero, "Proxy exceeded six");
                Check.That(await normal.GetSendAllowanceAsync("lease", default) <= TimeSpan.Zero, "Completed lease can be reused to send");
            }
            finally { await Cleanup(db, key); }
        });
    }

    private static Task<bool> Cleanup(IDatabase db, string key) => CleanupCore(db, key);
    private static async Task<bool> CleanupCore(IDatabase db, string key)
    {
        await db.KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode",
            key + ":market-demand", key + ":market-direct", key + ":market-direct-next", key + ":leases" });
        return true;
    }

    private sealed class ScriptFault
    {
        public bool Armed, FailBefore, TargetComplete;
        public object? Invoke(MethodInfo method, object?[]? args, Func<object?> next)
        {
            if (!Armed || method.Name != "ScriptEvaluateAsync" || args?[0] is not string script
                || (TargetComplete ? !script.Contains("if redis.call('SREM'") : !script.Contains("redis.call('SADD'")))
                return next();
            Armed = false;
            if (FailBefore)
                return Task.FromException<RedisResult>(Failure());
            return LoseReply((Task<RedisResult>)next()!);
        }
        private static async Task<RedisResult> LoseReply(Task<RedisResult> pending)
        {
            await pending;
            throw Failure();
        }
        private static RedisConnectionException Failure() => new(ConnectionFailureType.SocketFailure, "Isolated injected failure");
    }
    private sealed class ExpiredBudget : IRobloxRequestBudget
    {
        public TimeSpan Allowance;
        public bool Completed;
        public Task<TimeSpan> TryAcquireAsync(string id, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task CompleteAsync(string id, CancellationToken ct)
        {
            Completed = true;
            return Task.CompletedTask;
        }
        public Task<TimeSpan> GetSendAllowanceAsync(string id, CancellationToken ct) => Task.FromResult(Allowance);
    }
}

public class ForwardProxy : DispatchProxy
{
    private object target = null!;
    private Func<MethodInfo, object?[]?, Func<object?>, object?> invoke = null!;
    public static T Wrap<T>(T target, Func<MethodInfo, object?[]?, Func<object?>, object?> invoke) where T : class
    {
        var result = Create<T, ForwardProxy>();
        var proxy = (ForwardProxy)(object)result;
        proxy.target = target;
        proxy.invoke = invoke;
        return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => invoke(method!, args, () =>
    {
        try
        {
            return method!.Invoke(target, args);
        }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    });
}
