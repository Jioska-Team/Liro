using Liro.Application.RobloxIntegration.Models;

namespace Liro.Application.RobloxIntegration;

public interface IRobloxCatalogClient
{
    Task<RobloxCatalogItem?> GetItemAsync(long assetId, CancellationToken cancellationToken = default);
}
