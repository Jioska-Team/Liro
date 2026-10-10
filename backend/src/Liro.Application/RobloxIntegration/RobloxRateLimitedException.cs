using System.Net;

namespace Liro.Application.RobloxIntegration;

public sealed class RobloxRateLimitedException(DateTimeOffset retryAtUtc) : HttpRequestException("Roblox requests are paused until the provider cooldown expires.", null, HttpStatusCode.TooManyRequests)
{
    public DateTimeOffset RetryAtUtc { get; } = retryAtUtc;
    public TimeSpan RemainingDelay => RetryAtUtc > DateTimeOffset.UtcNow ? RetryAtUtc - DateTimeOffset.UtcNow : TimeSpan.Zero;
}
