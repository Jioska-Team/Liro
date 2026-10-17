using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Models;
using Microsoft.Extensions.Logging;

namespace Liro.Infrastructure.Roblox;

// One instance per process, shared across transient typed HTTP clients.
public sealed class RobloxCatalogSession(TimeProvider clock, ILogger<RobloxCatalogSession>? logger = null)
{
    private readonly object sync = new();
    private readonly Dictionary<CatalogItemKey, RobloxCatalogItem> recent = [];
    private readonly Dictionary<string, object> inFlight = [];
    private long sharedQueryCount;
    public long SharedQueryCount => Interlocked.Read(ref sharedQueryCount);
    private sealed class SharedCall<T>
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Lazy<Task<T>> Work;
        public int Users;
        public SharedCall(Func<CancellationToken, Task<T>> action) => Work = new(() => action(Cancellation.Token));
    }
    public async Task<T> ShareAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kind = key.StartsWith("search:", StringComparison.Ordinal) ? "búsqueda" : key.StartsWith("batch:", StringComparison.Ordinal) ? "lote de detalles" : "detalle individual";
        // Never make a main scan inherit an ordinary caller's queue position.
        key = (RobloxRequestPriority.IsMainScan ? "main:" : RobloxRequestPriority.IsUrgent ? "urgent:" : RobloxRequestPriority.IsBackground ? "background:" : "ordinary:") + key;
        SharedCall<T> call;
        long sharedTotal = 0;
        lock (sync)
        {
            if (!inFlight.TryGetValue(key, out var existing)) inFlight[key] = call = new(action);
            else
            {
                call = (SharedCall<T>)existing;
                sharedTotal = Interlocked.Increment(ref sharedQueryCount);
            }
            call.Users++;
        }
        Task<T>? work = null;
        try
        {
            if (sharedTotal > 0)
                logger?.LogInformation("Consulta compartida: tipo {Kind}; proceso {Operation}; uniones acumuladas en este proceso: {SharedTotal}.", kind, RobloxRequestPriority.OperationName, sharedTotal);
            work = call.Work.Value;
            return await work.WaitAsync(ct);
        }
        finally
        {
            bool last;
            lock (sync)
            {
                last = --call.Users == 0;
                if (last) inFlight.Remove(key);
            }
            if (last)
            {
                if (work is null) call.Cancellation.Dispose();
                else
                {
                    if (!work.IsCompleted) call.Cancellation.Cancel();
                    _ = work.ContinueWith(done => { _ = done.Exception; call.Cancellation.Dispose(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }
    private string? token;
    public string? Token => Volatile.Read(ref token);
    public void SetToken(string value) => Interlocked.Exchange(ref token, value);
    public void RejectToken(string? value) => Interlocked.CompareExchange(ref token, null, value);
    public RobloxCatalogItem? Find(CatalogItemKey key) => FindRecent(key, TimeSpan.FromSeconds(20));
    public RobloxCatalogItem? FindRecent(CatalogItemKey key, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        lock (sync)
        {
            if (!recent.TryGetValue(key, out var item)) return null;
            var age = clock.GetUtcNow().UtcDateTime - item.ObservedAtUtc;
            if (age >= TimeSpan.Zero && age < maximumAge) return item;
            if (age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(1)) recent.Remove(key);
            return null;
        }
    }
    public void Store(RobloxCatalogItem item)
    {
        if (item.Details?.CreatorId is not > 0 || item.Details.CreatorType is not ("User" or "Group")) return;
        lock (sync)
        {
            var key = new CatalogItemKey(item.AssetId, item.ItemType);
            if (recent.TryGetValue(key, out var previous) && previous.ObservedAtUtc >= item.ObservedAtUtc) return;
            if (recent.Count >= 5000)
            {
                var cutoff = clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
                foreach (var stale in recent.Where(x => x.Value.ObservedAtUtc <= cutoff).Select(x => x.Key).ToArray()) recent.Remove(stale);
                if (recent.Count >= 5000) recent.Remove(recent.MinBy(x => x.Value.ObservedAtUtc).Key);
            }
            recent[key] = item;
        }
    }
}
