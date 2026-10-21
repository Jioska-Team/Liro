using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace Liro.Application.Alerts;

public sealed record BandwidthSnapshot(string Account, string Plan, DateTimeOffset CycleStart, DateTimeOffset CycleEnd,
    decimal UsedBytes, decimal LimitBytes, DateTimeOffset ObservedAt)
{
    public string CycleKey => $"{Account}:{Plan}:{CycleStart.UtcTicks}:{CycleEnd.UtcTicks}";
    public decimal Percent => LimitBytes == 0 ? 0 : UsedBytes / LimitBytes * 100;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Account) || string.IsNullOrWhiteSpace(Plan) || UsedBytes < 0 || LimitBytes < 0
            || CycleEnd <= CycleStart || ObservedAt < CycleStart || ObservedAt > CycleEnd)
            throw new InvalidDataException("Invalid bandwidth observation.");
    }
}
public sealed record PendingBandwidthAlert(string Marker, int Threshold, string Content, DateTimeOffset CreatedAt);

public sealed class BandwidthAlertStore(IBandwidthAlertStateStore persistence, ulong channelId)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public sealed class SavedState
    {
        public List<CycleState> Cycles { get; set; } = [];
    }
    public sealed class CycleState
    {
        public string Key { get; set; } = "";
        public int Delivered
        {
            get; set;
        }
        public DateTimeOffset LastObservedAt
        {
            get; set;
        }
        public PendingBandwidthAlert? Pending
        {
            get; set;
        }
    }
    public async Task ObserveAsync(BandwidthSnapshot usage, Func<PendingBandwidthAlert, CancellationToken, Task> send, CancellationToken ct)
    {
        usage.Validate();
        await gate.WaitAsync(ct);
        try
        {
            var state = await persistence.LoadAsync(ct);
            if (state.Cycles is null || state.Cycles.Count > 32 || state.Cycles.Any(x => x.Delivered is not (0 or 70 or 85 or 95)))
                throw new InvalidDataException("Invalid alert state.");
            var cycle = state.Cycles.SingleOrDefault(x => x.Key == usage.CycleKey);
            if (cycle is null)
            {
                state.Cycles = state.Cycles.OrderByDescending(x => x.LastObservedAt).Take(31).ToList();
                cycle = new()
                {
                    Key = usage.CycleKey
                };
                state.Cycles.Add(cycle);
            }
            if (usage.ObservedAt < cycle.LastObservedAt)
                return;
            var threshold = usage.Percent >= 95 ? 95 : usage.Percent >= 85 ? 85 : usage.Percent >= 70 ? 70 : 0;
            if (cycle.Pending is null && threshold > cycle.Delivered)
            {
                var marker = "liro-webshare-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{channelId}:{usage.CycleKey}:{threshold}")))[..24];
                var icon = threshold == 95 ? "🔴" : threshold == 85 ? "🟠" : "🟡";
                var content = string.Create(CultureInfo.InvariantCulture,
                    $"{icon} **Webshare: consumo de ancho de banda ≥ {threshold}%**\nConsumo: **{usage.UsedBytes / 1_000_000_000m:0.00} / {usage.LimitBytes / 1_000_000_000m:0.00} GB ({usage.Percent:0.0}%)**.\nFin del ciclo: <t:{usage.CycleEnd.ToUnixTimeSeconds()}:f>.\nDato del proveedor consultado: <t:{usage.ObservedAt.ToUnixTimeSeconds()}:f>.\nRevisa el ancho de banda disponible para evitar que mercado deje de actualizarse.\n-# {marker}");
                cycle.Pending = new(marker, threshold, content, DateTimeOffset.UtcNow);
                await SaveAsync(state, ct); // Durable intent precedes the external side effect.
            }
            if (cycle.Pending is { } pending)
            {
                await send(pending, ct);
                cycle.Delivered = Math.Max(cycle.Delivered, pending.Threshold);
                cycle.Pending = null;
            }
            cycle.LastObservedAt = usage.ObservedAt;
            state.Cycles = state.Cycles.OrderByDescending(x => x.LastObservedAt).Take(32).ToList();
            await SaveAsync(state, ct);
        }
        finally { gate.Release(); }
    }
    private Task SaveAsync(SavedState state, CancellationToken ct) => persistence.SaveAsync(state, ct);
}

public interface IBandwidthAlertStateStore
{
    Task<BandwidthAlertStore.SavedState> LoadAsync(CancellationToken ct);
    Task SaveAsync(BandwidthAlertStore.SavedState state, CancellationToken ct);
}

public interface IBandwidthUsageSource
{
    Task<BandwidthSnapshot> ReadAsync(string apiKey, DateTimeOffset now, CancellationToken ct);
}

public interface IBandwidthAlertDelivery
{
    Task SendAsync(PendingBandwidthAlert alert, CancellationToken ct);
}
