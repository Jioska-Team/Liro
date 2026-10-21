using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Repositories;

public sealed record CatalogPage(IReadOnlyList<Item> Items, bool HasMore);
public sealed record CatalogCursor(DateTime? CreatedAtUtc, Guid Id);
public sealed record BundleContents(Item Bundle, IReadOnlyList<Item> Components, IReadOnlyList<long> PendingAssetIds);

public interface ICatalogBrowseRepository
{
    Task<CatalogPage> BrowseAsync(int page, int pageSize, CancellationToken ct);
    Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CancellationToken ct);
    Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct);
    Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetBundlesDueAsync(DateTime before, int limit, CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetMissingComponentsAsync(int limit, CancellationToken ct);
    Task SynchronizeRelationsAsync(CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetBundlesMissingThumbnailAsync(CancellationToken ct);
    Task SaveBundleThumbnailAsync(long bundleId, string? url, DateTime checkedAt, CancellationToken ct);
}
