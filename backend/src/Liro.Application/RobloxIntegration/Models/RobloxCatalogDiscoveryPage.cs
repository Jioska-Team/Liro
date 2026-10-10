namespace Liro.Application.RobloxIntegration.Models;

public sealed record RobloxCatalogDiscoveryPage(IReadOnlyList<long> AssetIds, string? NextPageCursor);
