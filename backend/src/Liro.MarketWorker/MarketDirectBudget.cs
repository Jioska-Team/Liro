using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Liro.MarketWorker;

// Reserve atomically against the EXISTING direct-IP budget. Never gives market its own 18 slots.
// All direct callers share the global counter; a short demand lease reserves one available turn.
public sealed class MarketDirectBudget(IConnectionMultiplexer redis, RobloxRequestOptions settings, int capacity) : IRobloxRequestBudget
{
    private readonly RedisRobloxRequestBudget shared = new(redis, Options.Create(settings), capacity);
    private const string Reserve = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[1])
        local pause=redis.call('PTTL',KEYS[9])
        if pause == -1 then return 60000 end
        if pause > 0 then return pause end
        if redis.call('GET', KEYS[4]) ~= 'completion-v2' then return 1000 end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now - 60000)
        -- Do not withhold capacity from other callers while market has already used its turn.
        local spacing = tonumber(redis.call('GET', KEYS[6]) or '0') - now
        if spacing > 0 then return spacing end
        if redis.call('ZCARD', KEYS[5]) >= tonumber(ARGV[3]) then return 1000 end
        redis.call('SET', KEYS[7], ARGV[4], 'PX', 5000)
        if redis.call('SCARD', KEYS[3]) > 0 or redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 1000 end
        local wait = tonumber(redis.call('GET', KEYS[2]) or '0') - now
        if wait > 0 then return math.min(wait, 1000) end
        redis.call('SADD', KEYS[3], ARGV[4])
        redis.call('ZADD', KEYS[8], now + tonumber(ARGV[6]), ARGV[4])
        redis.call('DEL', KEYS[7])
        redis.call('SET', KEYS[2], now + tonumber(ARGV[5]), 'PX', math.max(1, tonumber(ARGV[5]) + 1000))
        redis.call('ZADD', KEYS[5], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[5], 180000)
        redis.call('SET', KEYS[6], now + 60000 / tonumber(ARGV[3]), 'PX', 180000)
        return 0
        """;

    public Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken ct)
        => shared.TryAcquireAsync(reservationId, ct, owner =>
        {
            var k = settings.BudgetKey;
            return redis.GetDatabase().ScriptEvaluateAsync(RedisRobloxRequestBudget.MissingStateGuard + "\n" + Reserve,
                [k, k + ":next", k + ":pending", k + ":mode", k + ":market-direct", k + ":market-direct-next", k + ":market-demand", k + ":leases", k + ":cooldown"],
                [(long)settings.BudgetWindow.TotalMilliseconds, settings.BudgetCapacity, capacity, owner,
                    (long)settings.MinimumInterval.TotalMilliseconds, RedisRobloxRequestBudget.LeaseMilliseconds]).WaitAsync(ct);
        });

    public Task CompleteAsync(string reservationId, CancellationToken ct) => shared.CompleteAsync(reservationId, ct);
    public Task<TimeSpan> GetSendAllowanceAsync(string reservationId, CancellationToken ct)
        => shared.GetSendAllowanceAsync(reservationId, ct);
    public Task PauseAsync(TimeSpan delay, CancellationToken ct) => shared.PauseAsync(delay, ct);
    public Task<TimeSpan> GetCooldownAsync(CancellationToken ct) => shared.GetCooldownAsync(ct);
}

public static class MarketFallbackPolicy
{
    public static bool UseDirect(bool enabled, int total, int unavailable, bool providerPaused)
        => enabled && !providerPaused && unavailable == total;
}
