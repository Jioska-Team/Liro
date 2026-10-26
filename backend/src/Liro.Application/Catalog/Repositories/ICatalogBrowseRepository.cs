using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Repositories;

public enum CatalogLimitedFilter { All, Limited, LimitedUnique, Limiteds }
public enum CatalogSort { Created, Name, Price, Rap, Sales }

public sealed record CatalogBrowseOptions(
    CatalogLimitedFilter Limited = CatalogLimitedFilter.All,
    string? Search = null,
    CatalogSort Sort = CatalogSort.Created,
    bool Descending = true)
{
    public bool IsDefault => Limited == CatalogLimitedFilter.All && string.IsNullOrEmpty(Search)
        && Sort == CatalogSort.Created && Descending;
}

public sealed record LatestCatalog(IReadOnlyList<Item> Items, IReadOnlyList<Item> Limiteds);
public sealed record CatalogPage(IReadOnlyList<Item> Items, bool HasMore, CatalogCursor? NextCursor = null);
public sealed record CatalogCursor(DateTime? CreatedAtUtc, Guid Id, string? Name = null, decimal? Value = null);
public sealed record BundleContents(Item Bundle, IReadOnlyList<Item> Components, IReadOnlyList<long> PendingAssetIds);

public interface ICatalogBrowseRepository
{
    Task<LatestCatalog> BrowseLatestAsync(CancellationToken ct) =>
        throw new NotSupportedException("Latest catalog queries are not supported.");
    Task<CatalogPage> BrowseAsync(int page, int pageSize, CancellationToken ct);
    Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CancellationToken ct);
    Task<CatalogPage> BrowseAsync(int page, int pageSize, CatalogBrowseOptions options, CancellationToken ct) =>
        options.IsDefault ? BrowseAsync(page, pageSize, ct) : throw new NotSupportedException("Filtered catalog queries are not supported.");
    Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CatalogBrowseOptions options, CancellationToken ct) =>
        options.IsDefault ? BrowseAfterAsync(after, pageSize, ct) : throw new NotSupportedException("Filtered catalog queries are not supported.");
    Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct);
    Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetBundlesDueAsync(DateTime before, int limit, CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetMissingComponentsAsync(int limit, CancellationToken ct);
    Task SynchronizeRelationsAsync(CancellationToken ct);
    Task<IReadOnlyList<CatalogItemKey>> GetBundlesMissingThumbnailAsync(CancellationToken ct);
    Task SaveBundleThumbnailAsync(long bundleId, string? url, DateTime checkedAt, CancellationToken ct);
}
