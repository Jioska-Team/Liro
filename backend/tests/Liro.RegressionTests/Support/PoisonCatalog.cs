using System.Text.Json;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;

namespace Liro.RegressionTests;

sealed class PoisonCatalog : IRobloxCatalogClient
{
    public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken ct = default) => id == 1 ? throw new JsonException("permanently malformed") : Task.FromResult<RobloxCatalogItem?>(new(id, "Item", null, ItemMarketStatus.Normal));
}
