using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Enums;

namespace Liro.Application.Catalog.Services;

// A copy may have a redacted owner. This does not identify deleted accounts or unavailable supply.
public sealed record ItemOwnedCopy(long Uaid, string? InstanceId, long? SerialNumber,
    long? OwnerId, string? OwnerType, string? OwnerName);
public sealed record ItemOwnerPage(string Status, IReadOnlyList<ItemOwnedCopy> Copies,
    string? NextCursor, string? PreviousCursor, DateTime? ObservedAtUtc)
{
    public string Source => "Roblox";
    public static ItemOwnerPage Unavailable => new("Unavailable", [], null, null, null);
}
public interface IRobloxOwnerClient
{
    Task<ItemOwnerPage> GetAsync(long assetId, string? cursor, CancellationToken cancellationToken);
}
public sealed class ItemOwnerService(IItemRepository repository, IRobloxOwnerClient client)
{
    public async Task<ItemOwnerPage?> GetAsync(Guid itemId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit != 10 || cursor is { Length: > 1024 } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("Owner pages require limit 10 and a valid cursor of at most 1024 characters.");
        var item = await repository.GetByIdAsync(itemId, cancellationToken);
        if (item is null) return null;
        if (item.ItemType != CatalogItemType.Asset
            || item.MarketStatus is not (ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique))
            return ItemOwnerPage.Unavailable;
        return await client.GetAsync(item.RobloxAssetId, string.IsNullOrEmpty(cursor) ? null : cursor, cancellationToken);
    }
}
