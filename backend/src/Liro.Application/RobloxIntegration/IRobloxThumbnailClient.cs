using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
namespace Liro.Application.RobloxIntegration;

public interface IRobloxThumbnailClient
{
    Task<string?> GetAssetThumbnailAsync(long assetId, CancellationToken cancellationToken = default);
    async Task<string?> GetThumbnailAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed client required for bundles.");
        }
        var result = await GetAssetThumbnailAsync(key.Id, cancellationToken);
        return result;
    }
}

