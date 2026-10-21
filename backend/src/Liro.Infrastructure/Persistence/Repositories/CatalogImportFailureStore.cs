using Liro.Domain.Catalog.Models;
using Liro.Application.Catalog.Services;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogImportFailureStore(LiroDbContext db) : ICatalogImportFailureStore
{
    public async Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken cancellationToken)
    {
        var isBlocked = await db.Set<CatalogImportFailure>()
            .AnyAsync(x => x.AssetId == key.Id && x.ItemType == key.Type
                && (x.DeadLetteredAtUtc != null || x.NextAttemptAtUtc > now), cancellationToken);
        var canAttempt = !isBlocked;

        return canAttempt;
    }

    public async Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        var assetIds = await db.Set<CatalogImportFailure>()
            .AsNoTracking()
            .Where(x => x.DeadLetteredAtUtc == null && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.NextAttemptAtUtc)
            .Select(x => new CatalogItemKey(x.AssetId, x.ItemType))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return assetIds;
    }

    public async Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken cancellationToken)
    {
        error = error.Length > 2000 ? error[..2000] : error;
        var next = now.AddMinutes(1);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "CatalogImportFailures" ("ItemType", "AssetId", "Attempts", "NextAttemptAtUtc", "Error")
            VALUES ({key.Type.ToString()}, {key.Id}, 1, {next}, {error})
            ON CONFLICT ("ItemType", "AssetId") DO UPDATE SET
                "Attempts" = "CatalogImportFailures"."Attempts" + 1,
                "Error" = EXCLUDED."Error",
                "NextAttemptAtUtc" = {now} + make_interval(secs => LEAST(3600, 30 * power(2, LEAST("CatalogImportFailures"."Attempts", 7)))::double precision),
                "DeadLetteredAtUtc" = COALESCE("CatalogImportFailures"."DeadLetteredAtUtc",
                    CASE WHEN "CatalogImportFailures"."Attempts" + 1 >= 8 THEN {now} ELSE NULL END)
            """,
            cancellationToken);
    }

    public async Task ResolveAsync(CatalogItemKey key, CancellationToken cancellationToken)
    {
        await db.Set<CatalogImportFailure>()
            .Where(x => x.AssetId == key.Id && x.ItemType == key.Type)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
