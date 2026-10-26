using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Api.Contracts;

public sealed class ItemResponseMapper(IItemMarketSummaryRepository repository)
{
    public async Task<ItemResponse> MapAsync(Item item, CancellationToken cancellationToken) => (await MapAsync(new[] { item }, cancellationToken))[0];

    public async Task<ItemResponse[]> MapAsync(IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return [];

        var limitedIds = items
            .Where(x => x.MarketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
            .Select(x => x.Id)
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<Guid, decimal?> rap = limitedIds.Length > 0 ? await repository.GetRapAsync(limitedIds, cancellationToken) : new Dictionary<Guid, decimal?>();

        var itemsSelects = items
            .Select(item => ItemResponse.From(item, rap.GetValueOrDefault(item.Id)))
            .ToArray();
        return itemsSelects;
    }
}
