using System.Net;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;

namespace Liro.RegressionTests;

sealed class RecoveringDiscovery : IRobloxCatalogDiscoveryClient
{
    private int calls;
    public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken ct = default)
    {
        if (++calls == 1)
        {
            throw new HttpRequestException("500", null, HttpStatusCode.InternalServerError);
        }

        return Task.FromResult(new RobloxCatalogDiscoveryPage([1], null));
    }

    public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken ct = default) => GetCatalogPageAsync(cursor, ct);
}
