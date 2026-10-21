using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogBrowseRepository(LiroDbContext db) : ICatalogBrowseRepository
{
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
        var items = await db.Items.AsNoTracking().Where(x => x.CreatorId == 1 && x.CreatorType == "User")
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
        var query = db.Items.AsNoTracking().Where(x => x.CreatorId == 1 && x.CreatorType == "User").Where(CatalogVisibility.Visible);
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

    public async Task<BundleContents?> GetBundleContentsAsync(long bundleId, CancellationToken ct)
    {
        var bundle = await db.Items.AsNoTracking().SingleOrDefaultAsync(x => x.ItemType == CatalogItemType.Bundle && x.RobloxAssetId == bundleId, ct);
        if (bundle is null)
            return null;
        // Read the durable references as well, so details are correct during background link reconciliation.
        var components = await db.Items.AsNoTracking().Where(x => x.ItemType == CatalogItemType.Asset
            && bundle.BundleComponentAssetIds.Contains(x.RobloxAssetId)).OrderBy(x => x.RobloxAssetId).ToListAsync(ct);
        return new(bundle, components, bundle.BundleComponentAssetIds.Except(components.Select(x => x.RobloxAssetId)).ToArray());
    }

    public async Task<IReadOnlyList<Item>> GetParentBundlesAsync(long assetId, CancellationToken ct) =>
        await db.Items.AsNoTracking().Where(x => x.ItemType == CatalogItemType.Bundle && x.BundleComponentAssetIds.Contains(assetId))
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
