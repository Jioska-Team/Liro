using Liro.Domain.Catalog.Models;

namespace Liro.Domain.Catalog.Entities;

public sealed class ItemMarketState
{
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
    public bool IsNew
    {
        get; private set;
    }

    private ItemMarketState()
    {
    }

    public ItemMarketState(Guid itemId, ItemMarketData data, DateTime observedAtUtc, string source)
    {
        ItemId = itemId;
        IsNew = true;
        Update(data, observedAtUtc, source);
    }

    public void Update(ItemMarketData data, DateTime observedAtUtc, string source)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (observedAtUtc.Kind != DateTimeKind.Utc || source.Length > 200)
        {
            throw new ArgumentException("A UTC observation and a source of at most 200 characters are required.");
        }

        data.Validate();
        Data = data;
        ObservedAtUtc = observedAtUtc;
        Source = source;
    }

    public void MarkPersisted()
    {
        IsNew = false;
    }
}
