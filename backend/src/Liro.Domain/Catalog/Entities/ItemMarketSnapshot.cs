using Liro.Domain.Catalog.Models;

namespace Liro.Domain.Catalog.Entities;

public sealed class ItemMarketSnapshot
{
    public Guid Id
    {
        get; private set;
    }
    public Guid ItemId
    {
        get; private set;
    }
    public ItemMarketData Data { get; private set; } = new();
    public DateTime ObservedAtUtc
    {
        get; private set;
    }
    public string Source { get; private set; } = string.Empty;

    private ItemMarketSnapshot()
    {
    }

    public ItemMarketSnapshot(ItemMarketState state)
    {
        Id = Guid.NewGuid();
        ItemId = state.ItemId;
        Data = state.Data;
        ObservedAtUtc = state.ObservedAtUtc;
        Source = state.Source;
    }
}
