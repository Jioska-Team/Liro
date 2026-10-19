using System.Diagnostics;

namespace Liro.MarketWorker;

// A lane remains occupied through reading and saving, so configuration aliases
// cannot start a second query through the same proxy.
public sealed class MarketLanePool(int capacity)
{
    private readonly Dictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly object sync = new();
    public bool TryStart(string route, Func<Task> operation)
    {
        lock (sync)
        {
            foreach (var key in active.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
            {
                active[key].GetAwaiter().GetResult();
                active.Remove(key);
            }
            if (active.ContainsKey(route) || active.Count >= capacity) return false;
            active.Add(route, Task.Run(operation));
            return true;
        }
    }
    public Task DrainAsync() { lock (sync) return Task.WhenAll(active.Values.ToArray()); }
}

// Serialize starts, not responses. The Redis window remains the aggregate cap.
public sealed class MarketDispatchGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long nextStart;
    public async Task<T> RunAsync<T>(TimeSpan spacing, Func<Task<Task<T>>> start, CancellationToken ct)
    {
        Task<T> pending;
        await gate.WaitAsync(ct);
        try
        {
            var delay = TimeSpan.FromSeconds((nextStart - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            ct.ThrowIfCancellationRequested();
            pending = await start();
            nextStart = Stopwatch.GetTimestamp() + (long)Math.Ceiling(spacing.TotalSeconds * Stopwatch.Frequency);
        }
        finally { gate.Release(); }
        return await pending;
    }
    public void Dispose() => gate.Dispose();
}
