using System.Net;
using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxRequestOptions
{
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumFallbackCooldown { get; set; } = TimeSpan.FromMinutes(15);
}

// Shared by catalog, discovery and thumbnails within this process, across HttpClient lifetimes.
public sealed class RobloxRequestGate(IOptions<RobloxRequestOptions> options, TimeProvider clock, ILogger<RobloxRequestGate> logger) : IDisposable
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private DateTimeOffset nextRequestAt;
    private DateTimeOffset retryAt;
    private int consecutiveLimits;
    public async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (now < retryAt)
            {
                throw new RobloxRateLimitedException(retryAt);
            }

            if (now < nextRequestAt)
            {
                await Task.Delay(nextRequestAt - now, clock, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(cancellationToken);
            now = clock.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                consecutiveLimits = Math.Min(consecutiveLimits + 1, 16);
                var fallback = TimeSpan.FromTicks((long)Math.Min(
                    options.Value.MaximumFallbackCooldown.Ticks,
                    options.Value.DefaultCooldown.Ticks * Math.Pow(2, consecutiveLimits - 1)));
                var header = response.Headers.RetryAfter;
                var delay = header?.Delta ?? (header?.Date - now);
                if (delay is null || delay <= TimeSpan.Zero)
                {
                    delay = fallback;
                }

                retryAt = now.Add(delay.Value);
                response.Dispose();
                logger.LogWarning("Roblox returned 429. All Roblox clients in this process pause until {RetryAtUtc}.", retryAt);
                throw new RobloxRateLimitedException(retryAt);
            }

            if (response.IsSuccessStatusCode)
            {
                consecutiveLimits = 0;
            }

            return response;
        }
        finally
        {
            nextRequestAt = clock.GetUtcNow().Add(options.Value.MinimumInterval);
            mutex.Release();
        }
    }

    public void Dispose() => mutex.Dispose();
}
