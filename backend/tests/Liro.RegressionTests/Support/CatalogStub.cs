using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;

namespace Liro.RegressionTests;

sealed class CatalogStub : IRobloxCatalogClient
{
    public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => Task.FromResult<RobloxCatalogItem?>(new(id, "Item", null, ItemMarketStatus.Normal));
}
