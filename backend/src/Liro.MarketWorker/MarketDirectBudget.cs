using System.Diagnostics;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Liro.MarketWorker;

// Reserve atomically against the EXISTING direct-IP budget. Never gives market its own 18 slots.
// Catalog callers keep their existing implementation and do not wait behind a market queue.
public sealed class MarketDirectBudget(IConnectionMultiplexer redis, RobloxRequestOptions settings, int capacity) : IRobloxRequestBudget
{
    private static readonly string Owner = Environment.MachineName + "|" + Environment.ProcessId + "|"
        + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + "|";
    private readonly RedisRobloxRequestBudget shared = new(redis, Options.Create(settings));
    private const string Reserve = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[1])
        if redis.call('GET', KEYS[4]) ~= 'completion-v2' then return 1000 end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now - 60000)
        -- Yield while any direct request is in flight, or fewer than six normal slots remain.
        if redis.call('SCARD', KEYS[3]) > 0 or redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) - 6 then return 1000 end
        local quiet = tonumber(redis.call('GET', KEYS[2]) or '0') + 3000 - now
        local newest = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        if #newest > 0 then quiet = math.max(quiet, tonumber(newest[2]) + 6000 - now) end
        local spacing = tonumber(redis.call('GET', KEYS[6]) or '0') - now
        if quiet > 0 or spacing > 0 then return math.max(quiet, spacing) end
        if redis.call('ZCARD', KEYS[5]) >= tonumber(ARGV[3]) then return 1000 end
        redis.call('SADD', KEYS[3], ARGV[4])
        redis.call('SET', KEYS[2], now + tonumber(ARGV[5]), 'PX', math.max(1, tonumber(ARGV[5]) + 1000))
        redis.call('ZADD', KEYS[5], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[5], 180000)
        redis.call('SET', KEYS[6], now + 60000 / tonumber(ARGV[3]), 'PX', 180000)
        return 0
        """;

    public async Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var k = settings.BudgetKey;
        // All keys use the existing {roblox} hash tag, including on clustered Valkey.
        var result = await redis.GetDatabase().ScriptEvaluateAsync(Reserve,
            [k, k + ":next", k + ":pending", k + ":mode", k + ":market-direct", k + ":market-direct-next"],
            [(long)settings.BudgetWindow.TotalMilliseconds, settings.BudgetCapacity, capacity, Owner + reservationId,
                (long)settings.MinimumInterval.TotalMilliseconds]);
        return TimeSpan.FromMilliseconds((long)result);
    }

    public async Task CompleteAsync(string reservationId, CancellationToken ct)
    {
        // Close the secondary window BEFORE releasing the shared in-flight reservation.
        // An ambiguous Redis failure leaves the shared reservation held (fail closed).
        var k = settings.BudgetKey;
        await redis.GetDatabase().ScriptEvaluateAsync("""
            local t = redis.call('TIME')
            local now = tonumber(t[1]) * 1000 + math.ceil(tonumber(t[2]) / 1000)
            redis.call('ZADD', KEYS[1], now, ARGV[1])
            redis.call('PEXPIRE', KEYS[1], 180000)
            redis.call('SET', KEYS[2], now + 60000 / tonumber(ARGV[2]), 'PX', 180000)
            return 1
            """, [k + ":market-direct", k + ":market-direct-next"], [Owner + reservationId, capacity]);
        await shared.CompleteAsync(reservationId, ct);
    }
}

public static class MarketFallbackPolicy
{
    public static bool UseDirect(bool enabled, int total, int unavailable, bool providerPaused)
        => enabled && !providerPaused && unavailable == total;
}
