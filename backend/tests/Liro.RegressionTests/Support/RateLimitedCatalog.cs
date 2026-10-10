using System.Net;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;

namespace Liro.RegressionTests;

sealed class RateLimitedCatalog : IRobloxCatalogClient
{
    public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
}
