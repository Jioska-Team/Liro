using System.Net;
using Liro.Application.RobloxIntegration;

namespace Liro.RegressionTests;

sealed class FailingThumbnail : IRobloxThumbnailClient
{
    public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken ct = default) => throw new HttpRequestException("upstream unavailable", null, HttpStatusCode.ServiceUnavailable);
}
