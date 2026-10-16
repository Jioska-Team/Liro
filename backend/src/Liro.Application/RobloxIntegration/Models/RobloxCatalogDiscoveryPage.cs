using Liro.Domain.Catalog.Models;
namespace Liro.Application.RobloxIntegration.Models;

public sealed record RobloxCatalogDiscoveryPage(IReadOnlyList<long> AssetIds, string? NextPageCursor)
{
    public int ReturnedCount { get; init; } = AssetIds.Count;
    public IReadOnlyList<CatalogItemKey> Items { get; init; } = AssetIds.Select(id => new CatalogItemKey(id)).ToArray();
}


