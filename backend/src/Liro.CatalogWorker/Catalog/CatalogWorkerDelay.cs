using Liro.Application.RobloxIntegration;

namespace Liro.CatalogWorker.Catalog;

internal static class CatalogWorkerDelay
{
    public static async Task<Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage> ReadPendingPageAsync(
        Func<CancellationToken, Task<Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage>> read, ILogger logger, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await read(ct);
            }
            catch (RobloxRateLimitedException exception)
            {
                logger.LogInformation("Se conserva la página pendiente, el orden de las búsquedas y el tiempo del recorrido.");
                await WaitForRateLimitAsync(exception, TimeSpan.Zero, logger, ct);
            }
        }
    }

    public static Task WaitForRateLimitAsync(RobloxRateLimitedException exception, TimeSpan retryDelay, ILogger logger, CancellationToken cancellationToken)
    {
        // The shared gate already resolves Retry-After or the fallback when it is absent.
        // Do not impose the general failure retry interval a second time on HTTP 429.
        var remaining = exception.RetryAtUtc - DateTimeOffset.UtcNow;
        var delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        logger.LogWarning("Reintento por límite de Roblox programado para {RetryAtUtc}; espera restante: {DelaySeconds} segundos. Se conserva el trabajo pendiente sin registrar un fallo del artículo.",
            exception.RetryAtUtc, delay.TotalSeconds);
        return Task.Delay(delay, cancellationToken);
    }
}
