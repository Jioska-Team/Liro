namespace Liro.Infrastructure.Roblox;

// The scope flows through HttpClient handlers without changing public API contracts.
public sealed class RobloxRequestPriority : IDisposable
{
    private static readonly AsyncLocal<string?> operation = new();
    public static string OperationName => IsMainScan ? "búsqueda principal" : operation.Value ?? (IsUrgent ? "conocidos o confirmación prioritaria" : IsBackground ? "imágenes" : "descubrimiento");
    public static IDisposable Operation(string name) => new OperationScope(name);
    private sealed class OperationScope : IDisposable
    {
        private readonly string? previous = operation.Value;
        public OperationScope(string name) => operation.Value = name;
        public void Dispose() => operation.Value = previous;
    }
    private static readonly AsyncLocal<bool> mainScan = new();
    private static int activeScans;
    public static bool IsMainScan => mainScan.Value;
    internal static bool HasActiveScan => Volatile.Read(ref activeScans) > 0;
    public static IDisposable MainScan() => new MainScanScope();
    private sealed class MainScanScope : IDisposable
    {
        private readonly bool previous = mainScan.Value;
        private bool disposed;
        public MainScanScope() { mainScan.Value = true; Interlocked.Increment(ref activeScans); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            mainScan.Value = previous;
            Interlocked.Decrement(ref activeScans);
        }
    }
    private static readonly AsyncLocal<bool> background = new();
    public static bool IsBackground => background.Value;
    public static IDisposable Background() => new BackgroundScope();
    private sealed class BackgroundScope : IDisposable
    {
        private readonly bool previous = background.Value;
        public BackgroundScope() => background.Value = true;
        public void Dispose() => background.Value = previous;
    }
    private static readonly AsyncLocal<bool> urgent = new();
    private readonly bool previous;
    public static bool IsUrgent => urgent.Value || mainScan.Value;
    public RobloxRequestPriority()
    {
        previous = urgent.Value;
        urgent.Value = true;
    }
    public void Dispose() => urgent.Value = previous;
}

internal sealed class RobloxRequestTurnstile : IDisposable
{
    private readonly object sync = new();
    private readonly Queue<TaskCompletionSource> urgent = new();
    private readonly Queue<TaskCompletionSource> main = new();
    private readonly Queue<TaskCompletionSource> ordinary = new();
    private readonly Queue<TaskCompletionSource> background = new();
    private bool held;
    private bool disposed;
    private readonly CancellationTokenSource shutdown = new();
    private int urgentStreak;
    private int foregroundStreak;
    private int mainStreak;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!held) { held = true; return; }
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (RobloxRequestPriority.IsBackground ? background : RobloxRequestPriority.IsMainScan ? main : RobloxRequestPriority.IsUrgent ? urgent : ordinary).Enqueue(waiter);
        }
        using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        await waiter.Task;
    }

    public void Release()
    {
        lock (sync)
        {
            if (disposed) return;
            while (main.Count + urgent.Count + ordinary.Count + background.Count > 0)
            {
                // During a scan, images wait longer, but never indefinitely (one in 25 grants).
                var backgroundLimit = RobloxRequestPriority.HasActiveScan || main.Count > 0 ? 24 : 8;
                var low = background.Count > 0 && (foregroundStreak >= backgroundLimit || main.Count + urgent.Count + ordinary.Count == 0);
                // At least one ordinary request per four grants when both queues have work.
                var high = main.Count + urgent.Count > 0 && (urgentStreak < 3 || ordinary.Count == 0);
                // Two main-search turns per known/priority turn while both have pending work.
                var primary = main.Count > 0 && (mainStreak < 2 || urgent.Count == 0);
                if (!(low ? background : high ? primary ? main : urgent : ordinary).Dequeue().TrySetResult()) continue;
                foregroundStreak = low ? 0 : Math.Min(24, foregroundStreak + 1);
                if (!low) urgentStreak = high ? Math.Min(3, urgentStreak + 1) : 0;
                if (!low && high) mainStreak = primary ? Math.Min(2, mainStreak + 1) : 0;
                return;
            }
            held = false;
            urgentStreak = 0;
            foregroundStreak = 0;
            mainStreak = 0;
        }
    }

    public void ReleaseAfter(TimeSpan delay, TimeProvider clock)
    {
        if (delay <= TimeSpan.Zero) { Release(); return; }
        // Keep the slot closed while the caller processes its response and queues its next page.
        // Selection happens only when the shared spacing has elapsed.
        lock (sync)
        {
            if (disposed) return;
            _ = ReleaseLaterAsync(delay, clock, shutdown.Token);
        }
    }

    private async Task ReleaseLaterAsync(TimeSpan delay, TimeProvider clock, CancellationToken token)
    {
        try { await Task.Delay(delay, clock, token); Release(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            foreach (var waiter in main.Concat(urgent).Concat(ordinary).Concat(background)) waiter.TrySetException(new ObjectDisposedException(nameof(RobloxRequestTurnstile)));
            main.Clear();
            urgent.Clear();
            ordinary.Clear();
            background.Clear();
        }
        shutdown.Dispose();
    }
}
