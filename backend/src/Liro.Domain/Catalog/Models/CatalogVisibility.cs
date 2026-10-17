using System.Linq.Expressions;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Domain.Catalog.Models;

public static class CatalogVisibility
{
    // Roblox AssetType: Head, Torso, RightArm, LeftArm, LeftLeg, RightLeg, DynamicHead.
    public static readonly Expression<Func<Item, bool>> Visible = item =>
        item.ItemType == CatalogItemType.Bundle || item.AssetTypeId == null ||
        (item.AssetTypeId != 17 && item.AssetTypeId != 27 && item.AssetTypeId != 28 &&
         item.AssetTypeId != 29 && item.AssetTypeId != 30 && item.AssetTypeId != 31 && item.AssetTypeId != 79);

    public static bool IsBodyPart(Item item) => item.ItemType == CatalogItemType.Asset
        && item.AssetTypeId is 17 or 27 or 28 or 29 or 30 or 31 or 79;
}
