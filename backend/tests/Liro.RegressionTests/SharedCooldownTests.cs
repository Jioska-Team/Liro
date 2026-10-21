using System.Net;
using System.Net.Http.Headers;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Roblox;
using Liro.MarketWorker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Liro.RegressionTests;

internal static class SharedCooldownTests
{
    public static void Register(RegressionSuite suite)
    {
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS");
        suite.AsyncTest("Shared429_CatalogPausesOtherProcessAndDirectMarket", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{cooldown-test-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = new RobloxRequestOptions { BudgetKey = key, MinimumInterval = TimeSpan.Zero };
            var a = new RedisRobloxRequestBudget(redis, Options.Create(options));
            var b = new RedisRobloxRequestBudget(redis, Options.Create(options));
            using var first = Gate(options, a);
            using var second = Gate(options, b);
            try
            {
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => first.SendAsync(_ => Task.FromResult(Limited(TimeSpan.FromSeconds(5))), default));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var sent = false;
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => second.SendAsync(_ =>
                {
                    sent = true;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }, timeout.Token));
                Check.That(!sent, "Another process sent during the shared pause");
                var direct = new MarketDirectBudget(redis, options, 1);
                Check.That(await direct.TryAcquireAsync("blocked-market", default) > TimeSpan.Zero, "Direct market bypassed catalog 429");
                Check.That(await redis.GetDatabase().SetLengthAsync(key + ":pending") == 0, "Pause created another reservation");
                Check.That(await redis.GetDatabase().SortedSetLengthAsync(key) == 1, "429 changed the existing request accounting");
            }
            finally { await Cleanup(redis.GetDatabase(), key); }
        });
        suite.AsyncTest("Shared429_PreReservedRequestCannotSendDuringPause", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{cooldown-test-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = new RobloxRequestOptions { BudgetKey = key, MinimumInterval = TimeSpan.Zero };
            var reserved = new RedisRobloxRequestBudget(redis, Options.Create(options));
            using var gate = Gate(options, new RedisRobloxRequestBudget(redis, Options.Create(options)));
            try
            {
                Check.That(await reserved.TryAcquireAsync("reserved", default) == TimeSpan.Zero, "Fixture cannot reserve");
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => gate.SendAsync(_ => Task.FromResult(Limited(TimeSpan.FromSeconds(5))), default));
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => RobloxRequestAdmission.CreateAsync(default, (reserved, "reserved")));
                await reserved.CompleteAsync("reserved", default);
                Check.That(await redis.GetDatabase().SortedSetLengthAsync(key) == 2, "Completion during pause lost accounting");
            }
            finally { await Cleanup(redis.GetDatabase(), key); }
        });
        suite.AsyncTest("Shared429_DirectMarketPausesCatalogAndPreservesLongestDelay", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{cooldown-test-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = new RobloxRequestOptions { BudgetKey = key, MinimumInterval = TimeSpan.Zero };
            var direct = new MarketDirectBudget(redis, options, 1);
            var catalog = new RedisRobloxRequestBudget(redis, Options.Create(options));
            using var gate = Gate(options, catalog);
            try
            {
                Check.That(await catalog.TryAcquireAsync("initialize", default) == TimeSpan.Zero, "Cannot initialize shared budget");
                await catalog.CompleteAsync("initialize", default);
                Check.That(await direct.TryAcquireAsync("reserved", default) == TimeSpan.Zero, "Cannot reserve direct request");
                using var response = Limited(TimeSpan.FromSeconds(10));
                await RobloxRateLimitResponse.PublishAsync(response, direct, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, default);
                await catalog.PauseAsync(TimeSpan.FromSeconds(1), default);
                Check.That(await catalog.GetCooldownAsync(default) > TimeSpan.FromSeconds(7), "Shorter pause replaced longer pause");
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => RobloxRequestAdmission.CreateAsync(default, (direct, "reserved")));
                var sent = false;
                await Check.ThrowsAsync<RobloxRateLimitedException>(() => gate.SendAsync(_ =>
                {
                    sent = true;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }, default));
                Check.That(!sent, "Catalog bypassed direct-market pause");
                using var success = new HttpResponseMessage(HttpStatusCode.OK);
                using var csrf = new HttpResponseMessage(HttpStatusCode.Forbidden);
                await RobloxRateLimitResponse.PublishAsync(success, catalog, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, default);
                await RobloxRateLimitResponse.PublishAsync(csrf, catalog, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, default);
                Check.That(await catalog.GetCooldownAsync(default) > TimeSpan.FromSeconds(7), "Success or CSRF cleared pause");
                await direct.CompleteAsync("reserved", default);
                Check.That(await redis.GetDatabase().SortedSetLengthAsync(key) == 2, "Shared pause lost global accounting");
                Check.That(await redis.GetDatabase().SortedSetLengthAsync(key + ":market-direct") == 1, "Shared pause lost direct accounting");
            }
            finally { await Cleanup(redis.GetDatabase(), key); }
        });
        suite.AsyncTest("Shared429_ExpiresWithoutResettingBudgetAndHonorsDateHeader", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:{cooldown-test-" + Guid.NewGuid().ToString("N") + "}:budget";
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = new RobloxRequestOptions { BudgetKey = key, MinimumInterval = TimeSpan.Zero };
            var budget = new RedisRobloxRequestBudget(redis, Options.Create(options));
            try
            {
                Check.That(await budget.TryAcquireAsync("before", default) == TimeSpan.Zero, "Cannot reserve");
                await budget.CompleteAsync("before", default);
                var now = DateTimeOffset.UtcNow;
                using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(8));
                var delay = await RobloxRateLimitResponse.PublishAsync(response, budget, TimeSpan.FromMinutes(1), now, default);
                Check.That(delay == TimeSpan.FromSeconds(8), "Date Retry-After ignored");
                // A separate isolated budget exercises natural expiry without altering the published pause.
                var shortOptions = new RobloxRequestOptions { BudgetKey = key + ":short", MinimumInterval = TimeSpan.Zero };
                await redis.GetDatabase().StringSetAsync(key + ":short:mode", "completion-v2");
                var shortBudget = new RedisRobloxRequestBudget(redis, Options.Create(shortOptions));
                try
                {
                    Check.That(await shortBudget.TryAcquireAsync("counted", default) == TimeSpan.Zero, "Independent IP budget paused");
                    await shortBudget.CompleteAsync("counted", default);
                    await shortBudget.PauseAsync(TimeSpan.FromMilliseconds(150), default);
                    Check.That(await shortBudget.TryAcquireAsync("blocked", default) > TimeSpan.Zero, "Short pause bypassed");
                    await Task.Delay(250);
                    Check.That(await shortBudget.TryAcquireAsync("after", default) == TimeSpan.Zero, "Expired pause blocked progress");
                    await shortBudget.CompleteAsync("after", default);
                    Check.That(await redis.GetDatabase().SortedSetLengthAsync(key + ":short") == 2, "Expiry reset request counters");
                }
                finally { await Cleanup(redis.GetDatabase(), key + ":short"); }
                Check.That(await redis.GetDatabase().SortedSetLengthAsync(key) == 1, "Publishing pause reset existing counter");
            }
            finally { await Cleanup(redis.GetDatabase(), key); }
        });
    }
    private static RobloxRequestGate Gate(RobloxRequestOptions options, IRobloxRequestBudget budget)
        => new(Options.Create(options), TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, budget);
    private static HttpResponseMessage Limited(TimeSpan delay)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
    private static async Task Cleanup(IDatabase db, string key)
        => await db.KeyDeleteAsync(new RedisKey[] {key,key+":pending",key+":leases",key+":mode",key+":next",
            key+":cooldown",key+":market-direct",key+":market-direct-next",key+":market-demand"});
}
