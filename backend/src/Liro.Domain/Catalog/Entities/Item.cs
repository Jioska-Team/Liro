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
    public CatalogItemType ItemType { get; private set; } = CatalogItemType.Asset;
    // Durable provider references also retain components that have not been imported yet.
    public long[] BundleComponentAssetIds { get; private set; } = [];
    public DateTime? BundleComponentsObservedAtUtc
    {
        get; private set;
    }

    public bool UpdateBundleComponents(IEnumerable<long> assetIds, DateTime observedAtUtc)
    {
        if (ItemType != CatalogItemType.Bundle || observedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Bundle components require a bundle and UTC observation time.");
        var ids = assetIds.Distinct().Order().ToArray();
        if (ids.Any(id => id <= 0) || ids.Length > 1000)
            throw new ArgumentException("Invalid component IDs.");
        if (BundleComponentsObservedAtUtc is { } previous && observedAtUtc <= previous)
            return false;
        BundleComponentAssetIds = ids;
        BundleComponentsObservedAtUtc = observedAtUtc;
        return true;
    }
    public int? BundleTypeId
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
    public DateTime? ThumbnailCheckedAtUtc
    {
        get; private set;
    }

    public void MarkThumbnailChecked(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC required.", nameof(now));
        ThumbnailCheckedAtUtc = now;
    }
    public DateTime FirstSeenAt
    {
        get; private set;
    }
    // Provider observation times, never Roblox creation/modification dates.
    public DateTime? MarketStatusObservedAtUtc { get; private set; }
    public DateTime? LimitedFirstObservedAtUtc { get; private set; }
    public DateTime? BecameLimitedAtUtc { get; private set; }
    public bool HasBeenLimited { get; private set; }
    // Manual presentation order for historical Limiteds; never a conversion or observation date.
    public DateTime? LimitedHistoricalOrderAtUtc { get; private set; }

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

    public Item(long robloxAssetId, string name, Guid? collectibleItemId = null, ItemMarketStatus marketStatus = ItemMarketStatus.Unknown, CatalogItemType itemType = CatalogItemType.Asset, DateTime? observedAtUtc = null)
    {
        new CatalogItemKey(robloxAssetId, itemType).Validate();
        ItemType = itemType;
        Validate(name, collectibleItemId, marketStatus);
        Id = Guid.NewGuid();
        RobloxAssetId = robloxAssetId;
        MarketplaceUrl = "https://www.roblox.com/" + (itemType == CatalogItemType.Bundle ? "bundles/" : "catalog/") + robloxAssetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Name = name.Trim();
        CollectibleItemId = collectibleItemId;
        MarketStatus = marketStatus;
        if (observedAtUtc is { Kind: not DateTimeKind.Utc })
            throw new ArgumentException("UTC observation time required.", nameof(observedAtUtc));
        FirstSeenAt = LastUpdatedAt = NextRefreshAt = DateTime.UtcNow;
        MarketStatusObservedAtUtc = observedAtUtc;
        HasBeenLimited = marketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique;
        if (HasBeenLimited)
            LimitedFirstObservedAtUtc = observedAtUtc ?? FirstSeenAt;
    }

    public bool UpdateCatalogData(string name, Guid? collectibleItemId, ItemMarketStatus marketStatus, DateTime? observedAtUtc = null)
    {
        Validate(name, collectibleItemId, marketStatus);
        var observed = observedAtUtc ?? DateTime.UtcNow;
        if (observed.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC observation time required.", nameof(observedAtUtc));
        if (IsCatalogObservationStale(observed))
            return false;

        var previous = MarketStatus;
        // An incomplete classification cannot erase a previously known state.
        if (marketStatus == ItemMarketStatus.Unknown && previous != ItemMarketStatus.Unknown)
            marketStatus = previous;
        name = name.Trim();
        var changed = Name != name || CollectibleItemId != collectibleItemId || previous != marketStatus;
        MarketStatusObservedAtUtc = observed;
        if (marketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique)
        {
            if (!HasBeenLimited && BecameLimitedAtUtc is null
                && previous is ItemMarketStatus.Normal or ItemMarketStatus.Collectible)
            {
                BecameLimitedAtUtc = observed;
                _domainEvents.Add(new ItemBecameLimited(Id, RobloxAssetId, observed));
            }
            // Initial imports and Unknown -> Limited observations are a baseline, not a conversion.
            LimitedFirstObservedAtUtc ??= observed;
        }
        HasBeenLimited |= previous is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique
            || marketStatus is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique;

        Name = name;
        CollectibleItemId = collectibleItemId;
        MarketStatus = marketStatus;
        if (changed)
            LastUpdatedAt = DateTime.UtcNow;
        return changed;
    }

    public bool IsCatalogObservationStale(DateTime observedAtUtc) =>
        (MarketStatusObservedAtUtc is { } observed && observedAtUtc <= observed)
        || (LastCheckedAt is { } checkedAt && observedAtUtc < checkedAt);

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

    public bool UpdateDetails(ItemCatalogDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.CreatorId <= 0 || details.AssetTypeId <= 0 || details.BundleTypeId <= 0
            || details.CreatedAtUtc is { Kind: not DateTimeKind.Utc }
            || details.Restrictions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Invalid catalog metadata.", nameof(details));
        }

        var createdAtUtc = RobloxCreatedAtUtc ?? details.CreatedAtUtc;
        var updatedAtUtc = RobloxUpdatedAtUtc ?? createdAtUtc;
        if (details.CreatedAtUtc is { } incomingDate && incomingDate > createdAtUtc && incomingDate > updatedAtUtc)
        {
            updatedAtUtc = incomingDate;
        }

        var restrictions = details.Restrictions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = Description != details.Description || CreatorId != details.CreatorId
            || CreatorType != details.CreatorType || CreatorName != details.CreatorName
            || AssetTypeId != details.AssetTypeId || BundleTypeId != details.BundleTypeId || !Restrictions.SequenceEqual(restrictions)
            || RobloxCreatedAtUtc != createdAtUtc
            || RobloxUpdatedAtUtc != updatedAtUtc;
        Description = details.Description;
        CreatorId = details.CreatorId;
        CreatorType = details.CreatorType;
        CreatorName = details.CreatorName;
        AssetTypeId = details.AssetTypeId;
        BundleTypeId = details.BundleTypeId;
        Restrictions = restrictions;
        RobloxCreatedAtUtc = createdAtUtc;
        RobloxUpdatedAtUtc = updatedAtUtc;
        if (changed)
        {
            LastUpdatedAt = DateTime.UtcNow;
        }
        return changed;
    }

    public void ObserveMarket(ItemMarketData data, DateTime observedAtUtc, string source)
    {
        if (MarketState is not null && observedAtUtc <= MarketState.ObservedAtUtc)
        {
            return;
        }

        var changed = MarketState is null || MarketState.Data != data;
        if (MarketState is null)
        {
            MarketState = new ItemMarketState(Id, data, observedAtUtc, source);
        }
        else
        {
            MarketState.Update(data, observedAtUtc, source);
        }

        // Always retain freshness and source, but only append a data change to history.
        if (changed)
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

