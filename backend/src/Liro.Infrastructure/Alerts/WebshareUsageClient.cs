using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Liro.Application.Alerts;
namespace Liro.Infrastructure.Alerts;

public sealed class WebshareApiException(string code, TimeSpan retryAfter) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan RetryAfter { get; } = retryAfter;
}
public sealed class WebshareUsageClient(HttpClient http) : IBandwidthUsageSource
{
    public async Task<BandwidthSnapshot> ReadAsync(string apiKey, DateTimeOffset now, CancellationToken ct)
    {
        using var subscription = await GetAsync("subscription/", apiKey, now, ct);
        var sub = subscription.RootElement;
        var id = sub.GetProperty("id").GetInt64();
        var plan = sub.GetProperty("plan").GetInt64();
        if (id <= 0 || plan <= 0)
            throw new InvalidDataException("Invalid subscription identifiers.");
        var start = sub.GetProperty("start_date").GetDateTimeOffset();
        var end = sub.GetProperty("end_date").GetDateTimeOffset();
        if (now < start || now > end)
            throw new InvalidDataException("Subscription period is not current.");
        using var details = await GetAsync($"subscription/plan/{plan}/", apiKey, now, ct);
        if (details.RootElement.GetProperty("id").GetInt64() != plan || details.RootElement.GetProperty("status").GetString() != "active")
            throw new InvalidDataException("Inactive or mismatched plan.");
        var limit = details.RootElement.GetProperty("bandwidth_limit").GetDecimal() * 1_000_000_000m;
        // Never compare projected bandwidth with the actual quota.
        var from = Uri.EscapeDataString(start.ToUniversalTime().ToString("O"));
        var until = Uri.EscapeDataString(now.ToUniversalTime().ToString("O"));
        using var stats = await GetAsync($"stats/aggregate/?plan_id={plan}&timestamp__gte={from}&timestamp__lte={until}", apiKey, now, ct);
        var usage = new BandwidthSnapshot(id.ToString(System.Globalization.CultureInfo.InvariantCulture), plan.ToString(System.Globalization.CultureInfo.InvariantCulture),
            start, end, stats.RootElement.GetProperty("bandwidth_total").GetDecimal(), limit, now);
        usage.Validate();
        return usage;
    }
    private async Task<JsonDocument> GetAsync(string path, string apiKey, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://proxy.webshare.io/api/v2/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - now : TimeSpan.FromMinutes(15));
            throw new WebshareApiException("WebshareHTTP" + (int)response.StatusCode, wait > TimeSpan.Zero ? wait : TimeSpan.FromMinutes(5));
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
