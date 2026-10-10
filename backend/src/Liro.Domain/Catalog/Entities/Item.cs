using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Events;
using Liro.Domain.Catalog.Models;
using Liro.Domain.Common.Events;

namespace Liro.Domain.Catalog.Entities;

public sealed class Item : IHasDomainEvents
{
    public Guid Id
    {
        get; private set;
    }
    public long RobloxAssetId
    {
        get; private set;
    }
    public Guid? CollectibleItemId
    {
        get; private set;
    }
    public string Name { get; private set; } = string.Empty;
    public string MarketplaceUrl { get; private set; } = string.Empty;
    public string? Description
    {
        get; private set;
    }
    public long? CreatorId
    {
        get; private set;
    }
    public string? CreatorType
    {
        get; private set;
    }
    public string? CreatorName
    {
        get; private set;
    }
    public int? AssetTypeId
    {
        get; private set;
    }
    public string[] Restrictions { get; private set; } = [];
    public DateTime? RobloxCreatedAtUtc
    {
        get; private set;
    }
    public DateTime? RobloxUpdatedAtUtc
    {
        get; private set;
    }
    public ItemMarketState? MarketState
    {
        get; private set;
    }
    private readonly List<ItemMarketSnapshot> _pendingMarketSnapshots = [];
    public IReadOnlyCollection<ItemMarketSnapshot> PendingMarketSnapshots => _pendingMarketSnapshots.AsReadOnly();
    public ItemMarketStatus MarketStatus
    {
        get; private set;
    }
    public string? ThumbnailUrl
    {
        get; private set;
    }
    public DateTime? ThumbnailUpdatedAt
    {
        get; private set;
    }
    public DateTime FirstSeenAt
    {
        get; private set;
    }
    public DateTime LastUpdatedAt
    {
        get; private set;
    }
    public DateTime? LastCheckedAt
    {
        get; private set;
    }
    public DateTime NextRefreshAt
    {
        get; private set;
    }
    public uint Version
    {
        get; private set;
    }

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    private Item()
    {
    }

    public Item(long robloxAssetId, string name, Guid? collectibleItemId = null, ItemMarketStatus marketStatus = ItemMarketStatus.Unknown)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(robloxAssetId);
        Validate(name, collectibleItemId, marketStatus);
        Id = Guid.NewGuid();
        RobloxAssetId = robloxAssetId;
        MarketplaceUrl = "https://www.roblox.com/catalog/" + robloxAssetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Name = name.Trim();
        CollectibleItemId = collectibleItemId;
        MarketStatus = marketStatus;
        FirstSeenAt = LastUpdatedAt = NextRefreshAt = DateTime.UtcNow;
    }

    public bool UpdateCatalogData(string name, Guid? collectibleItemId, ItemMarketStatus marketStatus)
    {
        Validate(name, collectibleItemId, marketStatus);
        name = name.Trim();
        if (Name == name && CollectibleItemId == collectibleItemId && MarketStatus == marketStatus)
        {
            return false;
        }

        var previous = MarketStatus;
        Name = name;
        CollectibleItemId = collectibleItemId;
        MarketStatus = marketStatus;
        LastUpdatedAt = DateTime.UtcNow;
        if (previous is not (ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
            && marketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
        {
            _domainEvents.Add(new ItemBecameLimited(Id, RobloxAssetId, LastUpdatedAt));
        }

        return true;
    }

    public void MarkChecked(DateTime checkedAtUtc, TimeSpan refreshInterval)
    {
        if (checkedAtUtc.Kind != DateTimeKind.Utc || refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp and positive refresh interval are required.");
        }

        LastCheckedAt = checkedAtUtc;
        NextRefreshAt = checkedAtUtc.Add(refreshInterval);
    }

    public bool UpdateThumbnail(string? thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return false;
        }

        if (thumbnailUrl.Length > 1000 || !Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Thumbnail must be an HTTPS URL of at most 1000 characters.", nameof(thumbnailUrl));
        }

        if (ThumbnailUrl == thumbnailUrl)
        {
            return false;
        }

        ThumbnailUrl = thumbnailUrl;
        ThumbnailUpdatedAt = DateTime.UtcNow;
        return true;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    public void UpdateDetails(ItemCatalogDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.CreatorId <= 0 || details.AssetTypeId <= 0
            || details.CreatedAtUtc is { Kind: not DateTimeKind.Utc }
            || details.UpdatedAtUtc is { Kind: not DateTimeKind.Utc }
            || details.Restrictions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Invalid catalog metadata.", nameof(details));
        }

        var restrictions = details.Restrictions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = Description != details.Description || CreatorId != details.CreatorId
            || CreatorType != details.CreatorType || CreatorName != details.CreatorName
            || AssetTypeId != details.AssetTypeId || !Restrictions.SequenceEqual(restrictions)
            || RobloxCreatedAtUtc != details.CreatedAtUtc
            || (details.UpdatedAtUtc.HasValue && RobloxUpdatedAtUtc != details.UpdatedAtUtc);
        Description = details.Description;
        CreatorId = details.CreatorId;
        CreatorType = details.CreatorType;
        CreatorName = details.CreatorName;
        AssetTypeId = details.AssetTypeId;
        Restrictions = restrictions;
        RobloxCreatedAtUtc = details.CreatedAtUtc;
        RobloxUpdatedAtUtc = details.UpdatedAtUtc ?? RobloxUpdatedAtUtc;
        if (changed)
        {
            LastUpdatedAt = DateTime.UtcNow;
        }
    }

    public void ObserveMarket(ItemMarketData data, DateTime observedAtUtc, string source)
    {
        if (MarketState is not null && observedAtUtc <= MarketState.ObservedAtUtc)
        {
            return;
        }

        if (MarketState is null)
        {
            MarketState = new ItemMarketState(Id, data, observedAtUtc, source);
        }
        else
        {
            MarketState.Update(data, observedAtUtc, source);
        }

        _pendingMarketSnapshots.Add(new ItemMarketSnapshot(MarketState));
    }

    public void MarkMarketPersisted()
    {
        MarketState?.MarkPersisted();
        _pendingMarketSnapshots.Clear();
    }
    private static void Validate(string name, Guid? collectibleId, ItemMarketStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 200)
        {
            throw new ArgumentException("Name must be at most 200 characters.", nameof(name));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (collectibleId == Guid.Empty)
        {
            throw new ArgumentException("Collectible ID cannot be empty.", nameof(collectibleId));
        }
    }
}
