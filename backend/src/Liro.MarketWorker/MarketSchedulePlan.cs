namespace Liro.MarketWorker;

public sealed class MarketPopulation
{
    public string Activity { get; set; } = "SinDatos";
    public int Count { get; set; }
}

public static class MarketSchedulePlan
{
    // Leave 10% for latency/retries and one in four selections for background coverage.
    public static MarketOptions Build(MarketOptions baseline, IReadOnlyList<MarketPopulation> population, int routeCapacity)
    {
        var counts = population.ToDictionary(x => x.Activity, x => x.Count);
        int Count(string activity) => counts.GetValueOrDefault(activity);
        var usable = Math.Max(0.1, Math.Min(baseline.RequestsPerMinute, routeCapacity) * 0.9);
        var active = usable * 0.75;
        var very = Math.Max(baseline.VeryActiveMinutes, (int)Math.Ceiling(Count("MuyAlta") / (active / 2)));
        var high = Math.Max(baseline.ActiveMinutes, (int)Math.Ceiling(Count("Alta") / Math.Max(0.05, active - (double)Count("MuyAlta") / very)));
        var backgroundDemand = (double)Count("Media") / baseline.NormalMinutes
            + (double)Count("Baja") / baseline.LowMinutes
            + (double)(Count("Inactiva") + Count("SinVentasRecientes")) / baseline.InactiveMinutes
            + (double)Count("InciertaReciente") / baseline.UncertainMinutes
            + (double)(Count("SinHistorial") + Count("SinDatos")) / baseline.MissingHistoryMinutes;
        var scale = Math.Max(1, backgroundDemand / (usable * 0.25));
        return new MarketOptions {
            Enabled = baseline.Enabled, RequestsPerMinute = baseline.RequestsPerMinute,
            MaxConcurrentRequests = baseline.MaxConcurrentRequests,
            VeryActiveMinutes = very, ActiveMinutes = Math.Max(very, high),
            NormalMinutes = Math.Max(high, (int)Math.Ceiling(baseline.NormalMinutes * scale)),
            LowMinutes = Math.Max(high, (int)Math.Ceiling(baseline.LowMinutes * scale)),
            InactiveMinutes = Math.Max(high, (int)Math.Ceiling(baseline.InactiveMinutes * scale)),
            UncertainMinutes = Math.Max(high, (int)Math.Ceiling(baseline.UncertainMinutes * scale)),
            MissingHistoryMinutes = Math.Max(high, (int)Math.Ceiling(baseline.MissingHistoryMinutes * scale))
        };
    }
}
