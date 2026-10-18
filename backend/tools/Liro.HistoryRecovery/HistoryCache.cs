using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Liro.HistoryRecovery;

public sealed record CachedHistory(string ItemType, long RobloxId, string SourceUrl, DateTime FetchedAtUtc, JsonElement SalesData)
{
    public static string Url(string type, long id) => type switch
    {
        "Asset" => $"https://www.rolimons.com/item/{id}",
        "Bundle" => $"https://www.rolimons.com/bundle/{id}",
        _ => throw new ArgumentException("Tipo de artículo desconocido.")
    };
    public IReadOnlyList<HistoryDay> Validate(string type, long id)
    {
        if(ItemType != type || RobloxId != id || SourceUrl != Url(type,id) || FetchedAtUtc.Kind != DateTimeKind.Utc
            || FetchedAtUtc > DateTime.UtcNow.AddMinutes(5)) throw new JsonException("El archivo no corresponde al artículo o a la fuente.");
        return HistorySeries.Parse(SalesData.GetRawText(), FetchedAtUtc);
    }
    public static CachedHistory FromHtml(string html, string type, long id, DateTime fetchedAt)
    {
        var identity = type == "Asset" ? "item_id" : "bundle_id";
        if(!Regex.IsMatch(html, $@"\bvar\s+{identity}\s*=\s*{id}\s*;", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new JsonException("La página no identifica el artículo solicitado.");
        var match = Regex.Match(html, @"\bvar\s+sales_data\s*=\s*(\{[^\r\n]+\})\s*;", RegexOptions.None, TimeSpan.FromSeconds(1));
        if(!match.Success) throw new JsonException("La página no contiene una serie verificable.");
        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var result = new CachedHistory(type,id,Url(type,id),fetchedAt,doc.RootElement.Clone());
        result.Validate(type,id);
        return result;
    }
}

public sealed class SourceHttpException(int status, TimeSpan retryAfter) : Exception("HTTP" + status)
{
    public int Status { get; } = status;
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class HistoryDownloader : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    public HistoryDownloader() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Liro-HistoryRecovery/1.0");
    public async Task<CachedHistory> DownloadAsync(string type, long id, CancellationToken ct)
    {
        using var response = await http.GetAsync(CachedHistory.Url(type,id), ct);
        if(!response.IsSuccessStatusCode)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(15);
            throw new SourceHttpException((int)response.StatusCode, delay > TimeSpan.FromSeconds(15) ? delay : TimeSpan.FromSeconds(15));
        }
        return CachedHistory.FromHtml(await response.Content.ReadAsStringAsync(ct), type,id,DateTime.UtcNow);
    }
    public void Dispose() => http.Dispose();
}
