using Liro.Application.RobloxIntegration;

namespace Liro.Workers.Catalog;

internal static class CatalogWorkerDelay
{
    public static Task WaitForRateLimitAsync(RobloxRateLimitedException exception, TimeSpan retryDelay, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogWarning("Roblox traffic is paused until {RetryAtUtc}; no asset failure is recorded.", exception.RetryAtUtc);
        var delay = exception.RemainingDelay > retryDelay ? exception.RemainingDelay : retryDelay;
        return Task.Delay(delay, cancellationToken);
    }
}
