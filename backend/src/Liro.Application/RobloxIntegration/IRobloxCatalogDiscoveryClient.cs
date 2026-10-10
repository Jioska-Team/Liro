using Liro.Application.RobloxIntegration.Models;

namespace Liro.Application.RobloxIntegration;

public interface IRobloxCatalogDiscoveryClient
{
    Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default);
    Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default);
}
