using Liro.Application.RobloxIntegration.Models;
namespace Liro.Application.RobloxIntegration;

// Alternative details for assets explicitly referenced by an official bundle.
public interface IRobloxComponentClient
{
    Task<RobloxCatalogItem?> GetAssetAsync(long assetId, CancellationToken ct);
}
