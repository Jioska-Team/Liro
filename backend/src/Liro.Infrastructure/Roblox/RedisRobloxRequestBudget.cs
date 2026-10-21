using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using Liro.Application.RobloxIntegration;

namespace Liro.Infrastructure.Roblox;

public interface IRobloxRequestBudget
{
    // Zero reserves one attempt; a positive delay does not consume capacity.
    Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken cancellationToken);
    Task CompleteAsync(string reservationId, CancellationToken cancellationToken);
    Task<TimeSpan> GetSendAllowanceAsync(string reservationId, CancellationToken cancellationToken)
        => Task.FromResult(TimeSpan.FromSeconds(30));
    Task PauseAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    Task<TimeSpan> GetCooldownAsync(CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
}

// Shared by the API and all workers using the same Valkey. Server time avoids clock skew.
public sealed class RedisRobloxRequestBudget(IConnectionMultiplexer redis, IOptions<RobloxRequestOptions> options, int directCapacity = 0) : IRobloxRequestBudget
{
    private readonly ConcurrentDictionary<string, byte> unconfirmed = new();
    private static readonly string Host = Environment.MachineName;
    private static readonly string Owner = Host + "|" + Environment.ProcessId + "|" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + "|";
    private long nextRecovery;
    private long pauseUntil;
    // Only new reservations have a deadline. Legacy reservations are never expired by guessing.
    // Every HTTP caller validates its lease and bounds the send to 30 seconds before using it.
    public const int LeaseMilliseconds = 300_000;
    // Atomic with every budget operation, including the custom direct-market reserve.
    // Missing durable mode means either first start or total state loss; both fail closed.
    public const string MissingStateGuard = """
        if redis.call('EXISTS', KEYS[4]) == 0 then
            local quarantine = math.max(60000, tonumber(ARGV[1]))
            redis.call('SET', KEYS[4], 'completion-v2')
            redis.call('PSETEX', KEYS[1] .. ':recovery', quarantine, 'state-loss')
            local ttl = redis.call('PTTL', KEYS[9])
            if ttl ~= -1 and ttl < quarantine then
                redis.call('PSETEX', KEYS[9], quarantine, 'state-loss')
            end
        end
        """;
    private const string Recover = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.ceil(tonumber(time[2]) / 1000)
        local expired = redis.call('ZRANGEBYSCORE', KEYS[6], '-inf', now)
        for _, id in ipairs(expired) do
            if redis.call('SREM', KEYS[3], id) == 1 then
                redis.call('ZADD', KEYS[1], now, id)
                redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) + tonumber(ARGV[3]) + 1000)
                local next = math.max(now + tonumber(ARGV[3]), tonumber(redis.call('GET', KEYS[2]) or '0'))
                redis.call('SET', KEYS[2], next, 'PX', math.max(1, next - now + 1000))
            end
            redis.call('ZREM', KEYS[6], id)
        end
        """;
    private const string Reserve = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local window = tonumber(ARGV[1])
        local capacity = tonumber(ARGV[2])
        local spacing = tonumber(ARGV[3])
        if redis.call('GET', KEYS[4]) ~= 'completion-v2' then
            -- Upgrade existing reservations conservatively; never reset the live counter.
            local old = redis.call('ZRANGEBYSCORE', KEYS[1], now - window, '+inf')
            for _, id in ipairs(old) do redis.call('ZADD', KEYS[1], now, id) end
            if #old > 0 then redis.call('PEXPIRE', KEYS[1], window + spacing + 1000) end
            redis.call('SET', KEYS[4], 'completion-v2')
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        local pause = redis.call('PTTL', KEYS[9])
        if pause == -1 then return 60000 end
        if pause > 0 then return pause end
        -- Only an eligible, waiting direct-market request reserves a turn.
        -- This short lease is renewed by market and disappears on Stop, proxy recovery or failure.
        -- Existing reservations are never cancelled and the full global cap is never raised.
        if redis.call('EXISTS', KEYS[5]) == 1 then capacity = math.max(0, capacity - 1) end
        local wait = math.max(0, tonumber(redis.call('GET', KEYS[2]) or '0') - now)
        if redis.call('ZCARD', KEYS[1]) + redis.call('SCARD', KEYS[3]) >= capacity then
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            if #oldest > 0 then wait = math.max(wait, tonumber(oldest[2]) + window - now)
            else wait = math.max(wait, 1000) end
        end
        if wait > 0 then return wait end
        redis.call('SADD', KEYS[3], ARGV[4])
        redis.call('ZADD', KEYS[6], now + tonumber(ARGV[5]), ARGV[4])
        redis.call('SET', KEYS[2], now + spacing, 'PX', math.max(1, spacing + 1000))
        return 0
        """;

    private const string Complete = """
        if redis.call('SREM', KEYS[3], ARGV[4]) == 0 then
            -- An already-sent request may finish after its reservation was lost.
            -- Keep a full window after that completion, without shortening a 429.
            if redis.call('EXISTS', KEYS[1] .. ':recovery') == 1 then
                local quarantine = math.max(60000, tonumber(ARGV[1]))
                local ttl = redis.call('PTTL', KEYS[9])
                if ttl ~= -1 and ttl < quarantine then
                    redis.call('PSETEX', KEYS[9], quarantine, 'state-loss')
                end
                redis.call('PEXPIRE', KEYS[1] .. ':recovery', quarantine)
            end
            return 0
        end
        redis.call('ZREM', KEYS[6], ARGV[4])
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.ceil(tonumber(time[2]) / 1000)
        local window = tonumber(ARGV[1])
        local spacing = tonumber(ARGV[3])
        redis.call('ZADD', KEYS[1], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[1], window + spacing + 1000)
        local next = math.max(now + spacing, tonumber(redis.call('GET', KEYS[2]) or '0'))
        redis.call('SET', KEYS[2], next, 'PX', math.max(1, next - now + 1000))
        if tonumber(ARGV[6]) > 0 then
            redis.call('ZADD', KEYS[7], now, ARGV[4])
            redis.call('PEXPIRE', KEYS[7], 180000)
            local directNext = math.max(now + 60000 / tonumber(ARGV[6]), tonumber(redis.call('GET', KEYS[8]) or '0'))
            redis.call('SET', KEYS[8], directNext, 'PX', math.max(180000, directNext - now + 1000))
        end
        return 0
        """;

    public Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken cancellationToken)
        => TryAcquireAsync(reservationId, cancellationToken, id => EvaluateAsync(Reserve, id, cancellationToken));

    // Alternative admission rules (direct market) share the same ambiguous-outcome reconciliation.
    public async Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken cancellationToken,
        Func<string, Task<RedisResult>> reserve)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await FlushPauseAsync(cancellationToken);
        foreach (var id in unconfirmed.Keys)
            await CompleteAsync(id, cancellationToken);
        await RecoverExitedLocalProcessesAsync(cancellationToken);
        await EvaluateAsync(Recover + "\nreturn 0", "", cancellationToken);
        try
        {
            return TimeSpan.FromMilliseconds((long)await reserve(Owner + reservationId));
        }
        catch
        {
            // The server may have reserved capacity even if the acknowledgment was lost.
            unconfirmed.TryAdd(reservationId, 0);
            try
            {
                await CompleteAsync(reservationId, CancellationToken.None);
            }
            catch { /* Keep it queued for reconciliation before the next reservation. */ }
            throw;
        }
    }

    public async Task<TimeSpan> GetSendAllowanceAsync(string reservationId, CancellationToken cancellationToken)
    {
        await FlushPauseAsync(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var result = await EvaluateAsync("""
            local pause = redis.call('PTTL', KEYS[9])
            if pause == -1 then return -60000 end
            if pause > 0 then return -pause end
            local time = redis.call('TIME')
            local now = tonumber(time[1]) * 1000 + math.ceil(tonumber(time[2]) / 1000)
            local deadline = tonumber(redis.call('ZSCORE', KEYS[6], ARGV[4]) or '0')
            if redis.call('SISMEMBER', KEYS[3], ARGV[4]) == 0 then return 0 end
            -- Leave a full minute between the last permissible send and orphan recovery.
            return math.max(0, math.min(30000, deadline - now - 60000))
            """, Owner + reservationId, cancellationToken);
        if ((long)result < 0)
            throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddMilliseconds(-(long)result));
        return TimeSpan.FromMilliseconds((long)result) - Stopwatch.GetElapsedTime(started);
    }

    public async Task PauseAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        var deadline = checked(Environment.TickCount64 + (long)Math.Ceiling(delay.TotalMilliseconds));
        long previous;
        do
        {
            previous = Volatile.Read(ref pauseUntil);
            if (previous >= deadline)
                break;
        }
        while (Interlocked.CompareExchange(ref pauseUntil, deadline, previous) != previous);
        // Retain the local deadline even on an ambiguous write; retry before subsequent budget operations.
        await FlushPauseAsync(cancellationToken);
    }

    public async Task<TimeSpan> GetCooldownAsync(CancellationToken cancellationToken)
    {
        await FlushPauseAsync(cancellationToken);
        var started = Environment.TickCount64;
        var ttl = (long)await EvaluateAsync("""
            local ttl=redis.call('PTTL',KEYS[9])
            if ttl == -1 then return 60000 end
            return math.max(0,ttl)
            """, "", cancellationToken);
        // A process which observed Retry-After can restore its remaining deadline
        // if Valkey loses its data, even when the process that received 429 exits.
        if (ttl > 0)
            RememberPause(checked(started + ttl));
        return TimeSpan.FromMilliseconds(ttl);
    }

    private void RememberPause(long deadline)
    {
        long previous;
        do
        {
            previous = Volatile.Read(ref pauseUntil);
            if (previous >= deadline)
                return;
        }
        while (Interlocked.CompareExchange(ref pauseUntil, deadline, previous) != previous);
    }
    private async Task FlushPauseAsync(CancellationToken ct)
    {
        var remaining = Volatile.Read(ref pauseUntil) - Environment.TickCount64;
        if (remaining <= 0)
            return;
        await redis.GetDatabase().ScriptEvaluateAsync("""
            local ttl=redis.call('PTTL',KEYS[1])
            local requested=tonumber(ARGV[1])
            if ttl ~= -1 and ttl < requested then redis.call('PSETEX',KEYS[1],requested,'429') end
            return 1
            """, [options.Value.BudgetKey + ":cooldown"], [remaining]).WaitAsync(ct);
    }

    public async Task CompleteAsync(string reservationId, CancellationToken cancellationToken)
    {
        unconfirmed.TryAdd(reservationId, 0);
        await EvaluateAsync(Complete, Owner + reservationId, cancellationToken);
        unconfirmed.TryRemove(reservationId, out _);
    }

    private async Task RecoverExitedLocalProcessesAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref nextRecovery))
            return;
        Interlocked.Exchange(ref nextRecovery, now + 60_000);
        var pending = await redis.GetDatabase().SetMembersAsync(options.Value.BudgetKey + ":pending").WaitAsync(ct);
        foreach (var entry in pending)
        {
            var parts = entry.ToString().Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], Host, StringComparison.Ordinal)
                || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var started))
                continue;
            bool exited;
            try
            {
                using var process = Process.GetProcessById(pid);
                exited = process.HasExited || process.StartTime.ToUniversalTime().Ticks != started;
            }
            catch (ArgumentException) { exited = true; }
            catch (InvalidOperationException) { exited = true; }
            catch (System.ComponentModel.Win32Exception) { continue; }
            if (exited)
                await EvaluateAsync(Complete, entry.ToString(), ct);
        }
    }

    private Task<RedisResult> EvaluateAsync(string script, string reservationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = options.Value;
        // Recovery counts an expired lease as completed NOW; it never resets or frees the window.
        return redis.GetDatabase().ScriptEvaluateAsync(MissingStateGuard + "\n" + script,
            new RedisKey[] { value.BudgetKey, value.BudgetKey + ":next", value.BudgetKey + ":pending", value.BudgetKey + ":mode", value.BudgetKey + ":market-demand",
                value.BudgetKey + ":leases", value.BudgetKey + ":market-direct", value.BudgetKey + ":market-direct-next", value.BudgetKey + ":cooldown" },
            new RedisValue[] { (long)Math.Ceiling(value.BudgetWindow.TotalMilliseconds), value.BudgetCapacity,
                (long)Math.Ceiling(value.MinimumInterval.TotalMilliseconds), reservationId, LeaseMilliseconds, directCapacity })
            .WaitAsync(cancellationToken);
    }
}
