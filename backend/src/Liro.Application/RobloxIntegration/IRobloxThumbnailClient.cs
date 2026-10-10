namespace Liro.Application.RobloxIntegration;

public interface IRobloxThumbnailClient
{
    Task<string?> GetAssetThumbnailAsync(long assetId, CancellationToken cancellationToken = default);
}
