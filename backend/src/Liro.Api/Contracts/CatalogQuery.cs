using Liro.Application.Catalog.Repositories;

namespace Liro.Api.Contracts;

/// <summary>Parses the optional filters shared by both stored catalog queries.</summary>
public static class CatalogQuery
{
    public const string Description =
        "Optional limited: limited, limitedUnique, or limiteds (both). " +
        "q: literal case-insensitive name search or exact Roblox ID, at most 100 characters; surrounding whitespace is ignored. " +
        "sort: created (default), name, price (lowest resale price), rap, or sales (daily sales). " +
        "direction: asc or desc (default). Unknown values sort last; equal values sort by internal ID. " +
        "Omitting these options preserves the original catalog order.";

    public const string ValidationMessage =
        "Usa limited: limited, limitedUnique o limiteds; q de hasta 100 caracteres; sort: created, name, price, rap o sales; direction: asc o desc.";

    public static bool TryParse(string? limited, string? q, string? sort, string? direction,
        out CatalogBrowseOptions options)
    {
        var filter = limited?.Trim().ToLowerInvariant() switch
        {
            null => CatalogLimitedFilter.All,
            "limited" => CatalogLimitedFilter.Limited,
            "limitedunique" => CatalogLimitedFilter.LimitedUnique,
            "limiteds" => CatalogLimitedFilter.Limiteds,
            _ => (CatalogLimitedFilter)(-1)
        };
        var order = sort?.Trim().ToLowerInvariant() switch
        {
            null or "created" => CatalogSort.Created,
            "name" => CatalogSort.Name,
            "price" => CatalogSort.Price,
            "rap" => CatalogSort.Rap,
            "sales" => CatalogSort.Sales,
            _ => (CatalogSort)(-1)
        };
        var normalizedDirection = direction?.Trim().ToLowerInvariant();
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        options = new CatalogBrowseOptions(filter, search, order, normalizedDirection != "asc");
        return Enum.IsDefined(filter) && Enum.IsDefined(order)
            && normalizedDirection is null or "asc" or "desc"
            && (search is null || search.Length <= 100 && !search.Contains('\0'));
    }
}
