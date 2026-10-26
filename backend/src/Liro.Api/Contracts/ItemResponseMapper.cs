using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Api.Contracts;

public sealed class ItemResponseMapper(IItemMarketSummaryRepository repository)
{
    public async Task<ItemResponse> MapAsync(Item item, CancellationToken cancellationToken) =>
        (await MapAsync(new[] { item }, cancellationToken))[0];

    public async Task<ItemResponse[]> MapAsync(IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var ids = items.Where(item => item.MarketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
            .Select(item => item.Id).Distinct().ToArray();
        IReadOnlyDictionary<Guid, decimal?> rap = ids.Length == 0
            ? new Dictionary<Guid, decimal?>()
            : await repository.GetRapAsync(ids, cancellationToken);
        return items.Select(item => ItemResponse.From(item, rap.GetValueOrDefault(item.Id))).ToArray();
    }
}
