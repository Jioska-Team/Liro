using System.Text.Json;
using Liro.Infrastructure.Market;

namespace Liro.MarketWorker;

public sealed class MarketOptions
{
    public bool Enabled { get; set; }
    public int RequestsPerMinute { get; set; } = 18;
    public int VeryActiveMinutes { get; set; } = 2;
    public int ActiveMinutes { get; set; } = 3;
    public int NormalMinutes { get; set; } = 30;
    public int LowMinutes { get; set; } = 120;
    public int InactiveMinutes { get; set; } = 180;
    public bool IsValid() => RequestsPerMinute is >= 1 and <= 18 && VeryActiveMinutes >= 2
        && ActiveMinutes >= VeryActiveMinutes && NormalMinutes >= ActiveMinutes
        && LowMinutes >= NormalMinutes && InactiveMinutes >= LowMinutes && InactiveMinutes <= 1440;
    public (string Activity, TimeSpan Interval) Schedule(decimal? dailySales) => dailySales switch
    {
        null => ("SinDatos", TimeSpan.FromMinutes(LowMinutes)),
        >= 100 => ("MuyAlta", TimeSpan.FromMinutes(VeryActiveMinutes)),
        >= 10 => ("Alta", TimeSpan.FromMinutes(ActiveMinutes)),
        >= 1 => ("Media", TimeSpan.FromMinutes(NormalMinutes)),
        > 0 => ("Baja", TimeSpan.FromMinutes(LowMinutes)),
        _ => ("Inactiva", TimeSpan.FromMinutes(InactiveMinutes))
    };
}

public sealed record MarketProxy(string Address, string SecretReference, int RequestsPerMinute = 6)
{
    public static MarketProxy Parse(CatalogoGeneral entry)
    {
        entry.Validate();
        if (entry.Tipo != "CONEXION_MERCADO" || entry.TipoDato != "Json" || !entry.Activo)
            throw new ArgumentException("La conexión de mercado no está habilitada.");
        var value = JsonSerializer.Deserialize<MarketProxy>(entry.Valor) ?? throw new ArgumentException("Conexión vacía.");
        if (!Uri.TryCreate(value.Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != ""
            || string.IsNullOrWhiteSpace(value.SecretReference) || value.SecretReference.Length > 80
            || !value.SecretReference.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || value.RequestsPerMinute is < 1 or > 18)
            throw new ArgumentException("Proxy inválido; credenciales separadas y límite entre 1 y 18.");
        return value;
    }
}

public sealed record HistoricalPoint(string Metric, DateTime PeriodUtc, decimal Value);
public sealed record ResaleObservation(decimal? Rap, IReadOnlyList<HistoricalPoint> Points, DateTime ObservedAtUtc)
{
    public static ResaleObservation Parse(string json, DateTime now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("recentAveragePrice", out var rawRap)) throw new JsonException("Falta RAP.");
        var rap = rawRap.ValueKind == JsonValueKind.Null ? (decimal?)null : rawRap.GetDecimal();
        if (rap is < 0 or > 1_000_000_000_000m) throw new JsonException("RAP inválido.");
        var points = new Dictionary<(string, DateTime), HistoricalPoint>();
        foreach (var (field, metric) in new[] { ("priceDataPoints", "PrecioMedioVenta"), ("volumeDataPoints", "VolumenVentas") })
        {
            if (!root.TryGetProperty(field, out var series) || series.ValueKind != JsonValueKind.Array || series.GetArrayLength() > 10000)
                throw new JsonException("Serie histórica inválida.");
            foreach (var p in series.EnumerateArray())
            {
                var date = p.GetProperty("date").GetDateTimeOffset().UtcDateTime;
                var value = p.GetProperty("value").GetDecimal();
                if (value < 0 || value > 1_000_000_000_000m || date > now.AddMinutes(5)
                    || (metric == "VolumenVentas" && decimal.Truncate(value) != value)) throw new JsonException("Punto histórico inválido.");
                var point = new HistoricalPoint(metric, date, value);
                if (points.TryGetValue((metric, date), out var previous) && previous.Value != value) throw new JsonException("Puntos contradictorios.");
                points[(metric, date)] = point;
            }
        }
        if (rap is null && points.Count == 0) throw new JsonException("Sin datos de mercado.");
        return new(rap, points.Values.ToArray(), now);
    }

    // Do not interpret missing days as zero sales. Require seven explicit completed daily buckets.
    public decimal? DailySales()
    {
        var end = ObservedAtUtc.Date;
        var points = Points.Where(p => p.Metric == "VolumenVentas" && p.PeriodUtc >= end.AddDays(-7) && p.PeriodUtc < end).ToArray();
        if (points.Length != 7 || points.Any(p => p.PeriodUtc != p.PeriodUtc.Date) || points.Select(p => p.PeriodUtc.Date).Distinct().Count() != 7) return null;
        return points.Sum(p => p.Value) / 7;
    }
}
