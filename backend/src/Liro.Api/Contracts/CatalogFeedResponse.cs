namespace Liro.Api.Contracts;

/// <summary>A bounded catalog page. Pass NextCursor unchanged as after to continue.</summary>
public sealed record CatalogFeedResponse(IReadOnlyList<ItemResponse> Items, int PageSize, bool HasMore, string? NextCursor);
