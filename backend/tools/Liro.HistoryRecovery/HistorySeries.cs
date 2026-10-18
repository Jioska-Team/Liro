using System.Text.Json;
namespace Liro.HistoryRecovery;
public sealed record HistoryDay(DateTime DateUtc, decimal AveragePrice, long Volume);
public static class HistorySeries
{
    public static IReadOnlyList<HistoryDay> Parse(string json, DateTime now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var count = root.GetProperty("num_points").GetInt32();
            var timestamps = root.GetProperty("timestamp");
            var prices = root.GetProperty("avg_daily_sales_price");
            var volumes = root.GetProperty("sales_volume");
            if(count is < 0 or > 10000 || timestamps.GetArrayLength() != count || prices.GetArrayLength() != count || volumes.GetArrayLength() != count)
                throw new JsonException("Columnas históricas desalineadas.");
            var days = new Dictionary<DateTime, HistoryDay>();
            for(var n = 0; n < count; n++)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[n].GetInt64()).UtcDateTime;
                var price = prices[n].GetDecimal();
                var volume = volumes[n].GetDecimal();
                if(date < new DateTime(2006,1,1,0,0,0,DateTimeKind.Utc) || date != date.Date || date > now
                    || price is < 0 or > 1_000_000_000_000m || volume is < 0 or > 1_000_000_000_000m || decimal.Truncate(volume) != volume
                    || !days.TryAdd(date, new(date, price, (long)volume))) throw new JsonException("Punto histórico inválido o repetido.");
            }
            return days.Values.OrderBy(x => x.DateUtc).ToArray();
        }
        catch(Exception e) when(e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException or OverflowException)
        { throw new JsonException("Formato de historial inválido.", e); }
    }
}
