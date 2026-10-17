using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Infrastructure.Persistence;

public static class CatalogOwnershipQueries
{
    public static IQueryable<Item> ForeignItemsWithoutOfficialBundle(IQueryable<Item> items) =>
        items.Where(item => ((item.CreatorId > 0 && item.CreatorId != 1)
            || (item.CreatorType != null && item.CreatorType.ToLower() == "group"))
            && !(item.ItemType == CatalogItemType.Asset && items.Any(bundle =>
                bundle.ItemType == CatalogItemType.Bundle && bundle.CreatorId == 1 && bundle.CreatorType == "User"
                && bundle.BundleComponentAssetIds.Contains(item.RobloxAssetId))));
}
