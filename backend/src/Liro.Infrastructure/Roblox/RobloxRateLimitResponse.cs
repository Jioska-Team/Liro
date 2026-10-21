using System.Net;

namespace Liro.Infrastructure.Roblox;

public static class RobloxRateLimitResponse
{
    // Publish before releasing the in-flight reservation. A success or CSRF 403 never clears a pause.
    public static async Task<TimeSpan> PublishAsync(HttpResponseMessage response, IRobloxRequestBudget? budget,
        TimeSpan fallback, DateTimeOffset now, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
            return TimeSpan.Zero;
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date - now);
        var effective = delay is { } value && value > TimeSpan.Zero ? value : fallback;
        if (budget is not null)
            await budget.PauseAsync(effective, ct);
        return effective;
    }
}
