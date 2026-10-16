using Liro.Application.RobloxIntegration.Models;

namespace Liro.Application.RobloxIntegration;

public interface IRobloxCatalogDiscoveryClient
{
    Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default);
    Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default);
    async Task<RobloxCatalogDiscoveryPage> GetPageAsync(CatalogSearchScope scope, bool recent, string? cursor, CancellationToken cancellationToken)
    {
        var page = recent ? await GetRecentlyUpdatedPageAsync(cursor, cancellationToken) : await GetCatalogPageAsync(cursor, cancellationToken);
        return page;
    }
    async Task<RobloxCatalogDiscoveryPage> GetPartitionPageAsync(CatalogSearchPartition partition, string? cursor, CancellationToken cancellationToken)
    {
        var page = await GetPageAsync(partition.Scope, partition.SortType == 3, cursor, cancellationToken);
        return page;
    }
}


