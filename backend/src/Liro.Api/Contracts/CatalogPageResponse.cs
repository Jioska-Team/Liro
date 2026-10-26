namespace Liro.Api.Contracts;

/// <summary>The existing offset-based catalog page; HasMore indicates another page may exist.</summary>
public sealed record CatalogPageResponse(IReadOnlyList<ItemResponse> Items, int Page, int PageSize, bool HasMore);

/// <summary>Stored bundle relationships, including assets whose item data has not been imported yet.</summary>
public sealed record BundleComponentsResponse(
    ItemResponse Bundle, IReadOnlyList<ItemResponse> Components,
    IReadOnlyList<long> PendingAssetIds, DateTime? RelationshipsObservedAtUtc);

/// <summary>Up to 30 items and 30 Limiteds, each ordered by registration in Liro, newest first.</summary>
public sealed record LatestCatalogResponse(IReadOnlyList<ItemResponse> Items, IReadOnlyList<ItemResponse> Limiteds);
