using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;

namespace Liro.RegressionTests;

sealed class TwoPageDiscovery : IRobloxCatalogDiscoveryClient
{
    public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken ct = default) => Task.FromResult(cursor is null ? new RobloxCatalogDiscoveryPage([1], "page2") : new RobloxCatalogDiscoveryPage([2], null));
    public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken ct = default) => GetCatalogPageAsync(cursor, ct);
}
