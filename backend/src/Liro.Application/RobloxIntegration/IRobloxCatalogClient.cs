using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Application.RobloxIntegration.Models;

namespace Liro.Application.RobloxIntegration;

public interface IRobloxCatalogClient
{
    Task<RobloxCatalogItem?> GetItemAsync(long assetId, CancellationToken cancellationToken = default);
    async Task<RobloxCatalogItem?> GetItemAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed client required for bundles.");
        }
        var result = await GetItemAsync(key.Id, cancellationToken);
        return result;
    }
    async Task<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>> GetItemsAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<CatalogItemKey, RobloxCatalogResult>();
        foreach (var key in keys.Distinct())
        {
            try
            {
                var item = await GetItemAsync(key, cancellationToken);
                results[key] = new(item, item is null ? "Item omitted from catalog response." : null);
            }
            catch (System.Text.Json.JsonException exception)
            {
                results[key] = new(null, exception.Message);
            }
        }
        return results;
    }
}


