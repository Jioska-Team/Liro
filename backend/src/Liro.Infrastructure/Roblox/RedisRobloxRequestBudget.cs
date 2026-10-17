using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Liro.Infrastructure.Roblox;

public interface IRobloxRequestBudget
{
    // Zero reserves one attempt; a positive delay does not consume capacity.
    Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken cancellationToken);
    Task CompleteAsync(string reservationId, CancellationToken cancellationToken);
}

// Shared by the API and all workers using the same Valkey. Server time avoids clock skew.
public sealed class RedisRobloxRequestBudget(IConnectionMultiplexer redis, IOptions<RobloxRequestOptions> options) : IRobloxRequestBudget
{
    private readonly ConcurrentDictionary<string, byte> unconfirmed = new();
    private static readonly string Host = Environment.MachineName;
    private static readonly string Owner = Host + "|" + Environment.ProcessId + "|" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + "|";
    private long nextRecovery;
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
        local wait = math.max(0, tonumber(redis.call('GET', KEYS[2]) or '0') - now)
        if redis.call('ZCARD', KEYS[1]) + redis.call('SCARD', KEYS[3]) >= capacity then
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            if #oldest > 0 then wait = math.max(wait, tonumber(oldest[2]) + window - now)
            else wait = math.max(wait, 1000) end
        end
        if wait > 0 then return wait end
        redis.call('SADD', KEYS[3], ARGV[4])
        redis.call('SET', KEYS[2], now + spacing, 'PX', math.max(1, spacing + 1000))
        return 0
        """;

    private const string Complete = """
        if redis.call('SREM', KEYS[3], ARGV[4]) == 0 then return 0 end
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.ceil(tonumber(time[2]) / 1000)
        local window = tonumber(ARGV[1])
        local spacing = tonumber(ARGV[3])
        redis.call('ZADD', KEYS[1], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[1], window + spacing + 1000)
        local next = math.max(now + spacing, tonumber(redis.call('GET', KEYS[2]) or '0'))
        redis.call('SET', KEYS[2], next, 'PX', math.max(1, next - now + 1000))
        return 0
        """;

    public async Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in unconfirmed.Keys) await CompleteAsync(id, cancellationToken);
        await RecoverExitedLocalProcessesAsync(cancellationToken);
        try { return TimeSpan.FromMilliseconds((long)await EvaluateAsync(Reserve, Owner + reservationId, cancellationToken)); }
        catch
        {
            // The server may have reserved capacity even if the acknowledgment was lost.
            unconfirmed.TryAdd(reservationId, 0);
            try { await CompleteAsync(reservationId, CancellationToken.None); }
            catch { /* Keep it queued for reconciliation before the next reservation. */ }
            throw;
        }
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
        if (now < Volatile.Read(ref nextRecovery)) return;
        Interlocked.Exchange(ref nextRecovery, now + 60_000);
        var pending = await redis.GetDatabase().SetMembersAsync(options.Value.BudgetKey + ":pending").WaitAsync(ct);
        foreach (var entry in pending)
        {
            var parts = entry.ToString().Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], Host, StringComparison.Ordinal)
                || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var started)) continue;
            bool exited;
            try
            {
                using var process = Process.GetProcessById(pid);
                exited = process.HasExited || process.StartTime.ToUniversalTime().Ticks != started;
            }
            catch (ArgumentException) { exited = true; }
            catch (InvalidOperationException) { exited = true; }
            catch (System.ComponentModel.Win32Exception) { continue; }
            if (exited) await EvaluateAsync(Complete, entry.ToString(), ct);
        }
    }

    private Task<RedisResult> EvaluateAsync(string script, string reservationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = options.Value;
        // Pending reservations do not expire. Only acknowledged completion starts their window.
        return redis.GetDatabase().ScriptEvaluateAsync(script,
            new RedisKey[] { value.BudgetKey, value.BudgetKey + ":next", value.BudgetKey + ":pending", value.BudgetKey + ":mode" },
            new RedisValue[] { (long)Math.Ceiling(value.BudgetWindow.TotalMilliseconds), value.BudgetCapacity,
                (long)Math.Ceiling(value.MinimumInterval.TotalMilliseconds), reservationId })
            .WaitAsync(cancellationToken);
    }
}
