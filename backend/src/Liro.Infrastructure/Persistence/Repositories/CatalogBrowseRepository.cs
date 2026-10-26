using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogBrowseRepository(LiroDbContext db) : ICatalogBrowseRepository
{
    public async Task<LatestCatalog> BrowseLatestAsync(CancellationToken ct)
    {
        const int limit = 30;

        var visible = db.Items
            .AsNoTracking()
            .Include(x => x.MarketState)
            .Where(x => x.CreatorId == 1 && x.CreatorType == "User")
            .Where(CatalogVisibility.Visible);

        var items = await visible
            .OrderByDescending(x => x.FirstSeenAt)
            .ThenBy(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        var limiteds = await visible
        .Where(x => x.MarketStatus == ItemMarketStatus.Limited ||
                    x.MarketStatus == ItemMarketStatus.LimitedUnique)
        .OrderByDescending(x => x.BecameLimitedAtUtc.HasValue)
        .ThenByDescending(x => x.BecameLimitedAtUtc)
        .ThenByDescending(x => x.LimitedHistoricalOrderAtUtc)
        .ThenByDescending(x => x.FirstSeenAt)
        .ThenBy(x => x.Id)
        .Take(limit)
        .ToListAsync(ct);

        return new(items, limiteds);
    }

    public async Task<IReadOnlyList<CatalogItemKey>> GetBundlesMissingThumbnailAsync(CancellationToken ct) =>
        await db.Items.AsNoTracking().Where(x => x.ItemType == CatalogItemType.Bundle && x.ThumbnailUrl == null
            && (x.ThumbnailCheckedAtUtc == null || x.ThumbnailCheckedAtUtc < DateTime.UtcNow.AddHours(-1)))
            .OrderBy(x => x.ThumbnailCheckedAtUtc.HasValue).ThenBy(x => x.ThumbnailCheckedAtUtc).ThenBy(x => x.Id)
            .Take(2).Select(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType)).ToListAsync(ct);

    public async Task SaveBundleThumbnailAsync(long bundleId, string? url, DateTime checkedAt, CancellationToken ct)
    {
        var probe = new Item(bundleId, "Paquete", itemType: CatalogItemType.Bundle);
        probe.UpdateThumbnail(url);
        await db.Items.Where(x => x.ItemType == CatalogItemType.Bundle && x.RobloxAssetId == bundleId && x.ThumbnailUrl == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ThumbnailUrl, probe.ThumbnailUrl)
                .SetProperty(x => x.ThumbnailUpdatedAt, probe.ThumbnailUpdatedAt)
                .SetProperty(x => x.ThumbnailCheckedAtUtc, checkedAt), ct);
    }

    public async Task<CatalogPage> BrowseAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page));
        var items = await db.Items.AsNoTracking().Include(x => x.MarketState).Where(x => x.CreatorId == 1 && x.CreatorType == "User")
            .Where(CatalogVisibility.Visible).OrderByDescending(x => x.RobloxCreatedAtUtc.HasValue)
            .ThenByDescending(x => x.RobloxCreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize + 1).ToListAsync(ct);
        return new(items.Take(pageSize).ToArray(), items.Count > pageSize);
    }

    public async Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CancellationToken ct)
    {
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (after is { } position && (position.Id == Guid.Empty || position.CreatedAtUtc is { Kind: not DateTimeKind.Utc }))
            throw new ArgumentException("Invalid catalog cursor.", nameof(after));
        var query = db.Items.AsNoTracking().Include(x => x.MarketState).Where(x => x.CreatorId == 1 && x.CreatorType == "User").Where(CatalogVisibility.Visible);
        if (after is { CreatedAtUtc: { } created })
            query = query.Where(x => x.RobloxCreatedAtUtc == null || x.RobloxCreatedAtUtc < created
                || (x.RobloxCreatedAtUtc == created && x.Id.CompareTo(after.Id) > 0));
        else if (after is not null)
            query = query.Where(x => x.RobloxCreatedAtUtc == null && x.Id.CompareTo(after.Id) > 0);
        var items = await query.OrderByDescending(x => x.RobloxCreatedAtUtc.HasValue)
            .ThenByDescending(x => x.RobloxCreatedAtUtc).ThenBy(x => x.Id)
            .Take(pageSize + 1).ToListAsync(ct);
        return new(items.Take(pageSize).ToArray(), items.Count > pageSize);
    }


    public Task<CatalogPage> BrowseAsync(int page, int pageSize, CatalogBrowseOptions options, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page));
        options = NormalizeOptions(options);
        return options.IsDefault ? BrowseAsync(page, pageSize, ct)
            : ReadPageAsync(OrderRows(BuildRows(options), options).Skip((page - 1) * pageSize), pageSize, options, ct);
    }

    public Task<CatalogPage> BrowseAfterAsync(CatalogCursor? after, int pageSize, CatalogBrowseOptions options, CancellationToken ct)
    {
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        options = NormalizeOptions(options);
        if (options.IsDefault)
            return BrowseAfterAsync(after, pageSize, ct);
        if (after is not null && (after.Id == Guid.Empty
            || after.CreatedAtUtc is { Kind: not DateTimeKind.Utc }
            || (options.Sort == CatalogSort.Name && after.Name is null)))
            throw new ArgumentException("Invalid catalog cursor.", nameof(after));
        var rows = BuildRows(options);
        if (after is not null)
            rows = SeekRows(rows, after, options);
        return ReadPageAsync(OrderRows(rows, options), pageSize, options, ct);
    }

    private static CatalogBrowseOptions NormalizeOptions(CatalogBrowseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Limited) || !Enum.IsDefined(options.Sort) || options.Search?.Length > 100)
            throw new ArgumentException("Invalid catalog query.", nameof(options));
        var search = options.Search?.Trim();
        return options with { Search = string.IsNullOrEmpty(search) ? null : search };
    }

    private IQueryable<CatalogRow> BuildRows(CatalogBrowseOptions options)
    {
        var items = db.Items.AsNoTracking().Include(x => x.MarketState).Where(x => x.CreatorId == 1 && x.CreatorType == "User")
            .Where(CatalogVisibility.Visible);
        items = options.Limited switch
        {
            CatalogLimitedFilter.Limited => items.Where(x => x.MarketStatus == ItemMarketStatus.Limited),
            CatalogLimitedFilter.LimitedUnique => items.Where(x => x.MarketStatus == ItemMarketStatus.LimitedUnique),
            CatalogLimitedFilter.Limiteds => items.Where(x => x.MarketStatus == ItemMarketStatus.Limited
                || x.MarketStatus == ItemMarketStatus.LimitedUnique),
            _ => items
        };
        if (options.Search is { } search)
        {
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var hasId = long.TryParse(search, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var robloxId) && robloxId > 0;
            items = items.Where(x => EF.Functions.ILike(x.Name, pattern, "\\")
                || (hasId && x.RobloxAssetId == robloxId));
        }
        if (options.Sort == CatalogSort.Price)
            return items.Select(x => new CatalogRow { Item = x,
                Value = x.MarketState == null ? null : (decimal?)x.MarketState.Data.LowestResalePriceRobux });
        if (options.Sort is CatalogSort.Rap or CatalogSort.Sales)
        {
            var useRap = options.Sort == CatalogSort.Rap;
            return from item in items
                   join watch in db.Set<MarketWatch>().AsNoTracking() on item.Id equals watch.ItemId into watches
                   from watch in watches.DefaultIfEmpty()
                   select new CatalogRow { Item = item,
                       Value = watch == null ? null : useRap ? watch.RecentAveragePrice : watch.DailySales };
        }
        return items.Select(x => new CatalogRow { Item = x });
    }

    private static IQueryable<CatalogRow> SeekRows(IQueryable<CatalogRow> rows, CatalogCursor after, CatalogBrowseOptions options)
    {
        if (options.Sort == CatalogSort.Created)
        {
            if (after.CreatedAtUtc is not { } date)
                return rows.Where(x => x.Item.RobloxCreatedAtUtc == null && x.Item.Id.CompareTo(after.Id) > 0);
            return options.Descending
                ? rows.Where(x => x.Item.RobloxCreatedAtUtc == null || x.Item.RobloxCreatedAtUtc < date
                    || (x.Item.RobloxCreatedAtUtc == date && x.Item.Id.CompareTo(after.Id) > 0))
                : rows.Where(x => x.Item.RobloxCreatedAtUtc == null || x.Item.RobloxCreatedAtUtc > date
                    || (x.Item.RobloxCreatedAtUtc == date && x.Item.Id.CompareTo(after.Id) > 0));
        }
        if (options.Sort == CatalogSort.Name)
            return options.Descending
                ? rows.Where(x => string.Compare(EF.Functions.Collate(x.Item.Name, "C"), after.Name) < 0
                    || (EF.Functions.Collate(x.Item.Name, "C") == after.Name && x.Item.Id.CompareTo(after.Id) > 0))
                : rows.Where(x => string.Compare(EF.Functions.Collate(x.Item.Name, "C"), after.Name) > 0
                    || (EF.Functions.Collate(x.Item.Name, "C") == after.Name && x.Item.Id.CompareTo(after.Id) > 0));
        if (after.Value is not { } value)
            return rows.Where(x => x.Value == null && x.Item.Id.CompareTo(after.Id) > 0);
        return options.Descending
            ? rows.Where(x => x.Value == null || x.Value < value || (x.Value == value && x.Item.Id.CompareTo(after.Id) > 0))
            : rows.Where(x => x.Value == null || x.Value > value || (x.Value == value && x.Item.Id.CompareTo(after.Id) > 0));
    }

    private static IOrderedQueryable<CatalogRow> OrderRows(IQueryable<CatalogRow> rows, CatalogBrowseOptions options)
    {
        if (options.Sort == CatalogSort.Created)
        {
            var knownFirst = rows.OrderByDescending(x => x.Item.RobloxCreatedAtUtc.HasValue);
            return (options.Descending ? knownFirst.ThenByDescending(x => x.Item.RobloxCreatedAtUtc)
                : knownFirst.ThenBy(x => x.Item.RobloxCreatedAtUtc)).ThenBy(x => x.Item.Id);
        }
        if (options.Sort == CatalogSort.Name)
            return (options.Descending ? rows.OrderByDescending(x => EF.Functions.Collate(x.Item.Name, "C"))
                : rows.OrderBy(x => EF.Functions.Collate(x.Item.Name, "C"))).ThenBy(x => x.Item.Id);
        var valuedFirst = rows.OrderByDescending(x => x.Value.HasValue);
        return (options.Descending ? valuedFirst.ThenByDescending(x => x.Value)
            : valuedFirst.ThenBy(x => x.Value)).ThenBy(x => x.Item.Id);
    }

    private static async Task<CatalogPage> ReadPageAsync(IQueryable<CatalogRow> query, int pageSize, CatalogBrowseOptions options, CancellationToken ct)
    {
        var rows = await query.Take(pageSize + 1).ToListAsync(ct);
        var hasMore = rows.Count > pageSize;
        CatalogCursor? next = null;
        if (hasMore)
        {
            var last = rows[pageSize - 1];
            next = options.Sort switch
            {
                CatalogSort.Created => new(last.Item.RobloxCreatedAtUtc, last.Item.Id),
                CatalogSort.Name => new(null, last.Item.Id, Name: last.Item.Name),
                _ => new(null, last.Item.Id, Value: last.Value)
            };
        }
        return new(rows.Take(pageSize).Select(x => x.Item).ToArray(), hasMore, next);
    }

    private sealed class CatalogRow
    {
        public Item Item { get; init; } = null!;
        public decimal? Value { get; init; }
    }

    public async Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct)
    {
        var bundle = await db.Items.AsNoTracking().Include(x => x.MarketState).SingleOrDefaultAsync(x => x.ItemType == CatalogItemType.Bundle && x.RobloxAssetId == bundleId, ct);
        if (bundle is null)
            return null;
        // Read the durable references as well, so details are correct during background link reconciliation.
        var components = await db.Items.AsNoTracking().Include(x => x.MarketState).Where(x => x.ItemType == CatalogItemType.Asset
            && bundle.BundleComponentAssetIds.Contains(x.RobloxAssetId)).OrderBy(x => x.RobloxAssetId).ToListAsync(ct);
        return new(bundle, components, bundle.BundleComponentAssetIds.Except(components.Select(x => x.RobloxAssetId)).ToArray());
    }

    public async Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct) =>
        await db.Items.AsNoTracking().Include(x => x.MarketState).Where(x => x.ItemType == CatalogItemType.Bundle && x.BundleComponentAssetIds.Contains(assetId))
            .OrderBy(x => x.RobloxAssetId).ToListAsync(ct);

    public async Task<IReadOnlyList<CatalogItemKey>> GetBundlesDueAsync(DateTime before, int limit, CancellationToken ct) =>
        await db.Items.AsNoTracking().Where(x => x.ItemType == CatalogItemType.Bundle && x.CreatorId == 1 && x.CreatorType == "User"
            && (x.BundleComponentsObservedAtUtc == null || x.BundleComponentsObservedAtUtc < before)
            && !db.Set<CatalogImportFailure>().Any(f => f.ItemType == x.ItemType && f.AssetId == x.RobloxAssetId
                && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > DateTime.UtcNow)))
            .OrderBy(x => x.BundleComponentsObservedAtUtc.HasValue).ThenBy(x => x.BundleComponentsObservedAtUtc).ThenBy(x => x.Id)
            .Take(limit).Select(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType)).ToListAsync(ct);

    public async Task<IReadOnlyList<CatalogItemKey>> GetMissingComponentsAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var now = DateTime.UtcNow;
        var ids = await db.Database.SqlQuery<long>($"""
            SELECT DISTINCT part.id AS "Value" FROM "Items" b
            CROSS JOIN LATERAL unnest(b."BundleComponentAssetIds") AS part(id)
            WHERE b."ItemType"='Bundle' AND b."CreatorId"=1 AND b."CreatorType"='User'
              AND NOT EXISTS (SELECT 1 FROM "Items" c WHERE c."ItemType"='Asset' AND c."RobloxAssetId"=part.id)
              AND NOT EXISTS (SELECT 1 FROM "CatalogImportFailures" f WHERE f."ItemType"='Asset' AND f."AssetId"=part.id
                AND (f."DeadLetteredAtUtc" IS NOT NULL OR f."NextAttemptAtUtc">{now}))
            ORDER BY part.id LIMIT {limit}
            """).ToListAsync(ct);
        return ids.Select(id => new CatalogItemKey(id)).ToArray();
    }

    public Task SynchronizeRelationsAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        WITH removed AS (
          DELETE FROM "BundleComponents" r USING "Items" b, "Items" c
          WHERE r."BundleId"=b."Id" AND r."ComponentItemId"=c."Id"
            AND (b."ItemType"<>'Bundle' OR c."ItemType"<>'Asset' OR NOT(c."RobloxAssetId"=ANY(b."BundleComponentAssetIds")))
        )
        INSERT INTO "BundleComponents" ("BundleId","ComponentItemId")
        SELECT DISTINCT b."Id", c."Id" FROM "Items" b
        CROSS JOIN LATERAL unnest(b."BundleComponentAssetIds") AS part(id)
        JOIN "Items" c ON c."ItemType"='Asset' AND c."RobloxAssetId"=part.id
        WHERE b."ItemType"='Bundle' AND b."CreatorId"=1 AND b."CreatorType"='User'
        ON CONFLICT ("BundleId","ComponentItemId") DO NOTHING
        """, ct);
}
