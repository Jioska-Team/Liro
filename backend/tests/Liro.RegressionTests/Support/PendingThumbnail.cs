using Liro.Application.RobloxIntegration;

namespace Liro.RegressionTests;

sealed class PendingThumbnail : IRobloxThumbnailClient
{
    public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
