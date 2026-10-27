using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Enums;

namespace Liro.Application.Catalog.Services;

public sealed record ItemResellerOffer(string InstanceId, long? SellerId, string? SellerType,
    string? SellerName, long? SerialNumber, long PriceRobux);

public sealed record ItemResellerPage(string Status, IReadOnlyList<ItemResellerOffer> Offers,
    string? NextCursor, string? PreviousCursor, DateTime? ObservedAtUtc)
{
    public string Source => "Roblox";
    public static ItemResellerPage Unavailable => new("Unavailable", [], null, null, null);
}

public interface IRobloxResellerClient
{
    Task<ItemResellerPage> GetAsync(Guid collectibleId, string? cursor, CancellationToken cancellationToken);
}

public sealed class ItemResellerService(IItemRepository repository, IRobloxResellerClient client)
{
    public async Task<ItemResellerPage?> GetAsync(Guid itemId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit != 10 || cursor is { Length: > 1024 } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("Reseller pages require limit 10 and a valid cursor of at most 1024 characters.");
        var item = await repository.GetByIdAsync(itemId, cancellationToken);
        if (item is null) return null;
        if (item.ItemType != CatalogItemType.Asset || item.CollectibleItemId is not { } collectibleId
            || item.MarketStatus is not (ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique))
            return ItemResellerPage.Unavailable;
        return await client.GetAsync(collectibleId, string.IsNullOrEmpty(cursor) ? null : cursor, cancellationToken);
    }
}
