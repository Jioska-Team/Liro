using System.Diagnostics;
using System.Text.Json;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogPageWriter(LiroDbContext db, CatalogRefreshPolicy policy,
    ILogger<CatalogPageWriter> logger) : ICatalogPageWriter
{
    public async Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> observations, bool onlyNew, CancellationToken cancellationToken)
    {
        if (observations.Count > 120)
            throw new ArgumentOutOfRangeException(nameof(observations));
        if (observations.Count == 0)
            return new(0, 0, 0, []);
        foreach (var key in observations.Keys)
            key.Validate();
        var started = Stopwatch.GetTimestamp();
        var assets = observations.Keys.Where(x => x.Type == CatalogItemType.Asset).Select(x => x.Id).ToArray();
        var bundles = observations.Keys.Where(x => x.Type == CatalogItemType.Bundle).Select(x => x.Id).ToArray();
        var existing = await db.Items.AsNoTracking().Include(x => x.MarketState).AsSingleQuery()
            .Where(x => (x.ItemType == CatalogItemType.Asset && assets.Contains(x.RobloxAssetId))
                || (x.ItemType == CatalogItemType.Bundle && bundles.Contains(x.RobloxAssetId)))
            .ToListAsync(cancellationToken);
        var failures = await db.Set<CatalogImportFailure>().AsNoTracking()
            .Where(x => (x.ItemType == CatalogItemType.Asset && assets.Contains(x.AssetId))
                || (x.ItemType == CatalogItemType.Bundle && bundles.Contains(x.AssetId)))
            .ToListAsync(cancellationToken);
        var readMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var preparation = Stopwatch.GetTimestamp();
        var existingByKey = existing.ToDictionary(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType));
        var failuresByKey = failures.ToDictionary(x => new CatalogItemKey(x.AssetId, x.ItemType));
        var added = new List<Item>();
        var updated = new List<Item>();
        var resolvedKeys = new HashSet<CatalogItemKey>();
        var retry = new List<CatalogItemKey>();
        var skipped = 0;
        foreach (var (key, remote) in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((failuresByKey.TryGetValue(key, out var failure) && (failure.DeadLetteredAtUtc is not null || failure.NextAttemptAtUtc > DateTime.UtcNow))
                || (onlyNew && existingByKey.ContainsKey(key)))
            {
                skipped++;
                continue;
            }
            // Deletions have dependent records and use the existing individual transaction path.
            if (remote.Details?.CreatorId != 1 || !string.Equals(remote.Details.CreatorType, "User", StringComparison.OrdinalIgnoreCase))
            {
                retry.Add(key);
                continue;
            }
            try
            {
                if (remote.AssetId != key.Id || remote.ItemType != key.Type)
                    throw new JsonException("Roblox returned a different asset ID.");
                var previous = existingByKey.GetValueOrDefault(key);
                var (item, save, resolveFailure) = ImportRobloxItemService.ApplyObservation(previous, remote);
                if (resolveFailure)
                    resolvedKeys.Add(key);
                if (save)
                {
                    item.MarkChecked(DateTime.UtcNow, policy.Interval);
                    (previous is null ? added : updated).Add(item);
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                retry.Add(key);
                logger.LogWarning(exception, "Catalog observation {Key} requires individual processing.", key);
            }
        }
        var prepareMs = Stopwatch.GetElapsedTime(preparation).TotalMilliseconds;
        var saving = Stopwatch.GetTimestamp();
        foreach (var item in added)
            db.Items.Add(item);
        foreach (var item in updated)
            db.Items.Update(item);
        var resolved = failures.Where(x => resolvedKeys.Contains(new(x.AssetId, x.ItemType))).ToArray();
        db.RemoveRange(resolved);
        try
        {
            // One SaveChanges: items, market states, snapshots, outbox and resolved retries
            // participate in EF's implicit transaction and existing execution strategy.
            if (added.Count + updated.Count + resolved.Length > 0)
                await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.StringDataRightTruncation or PostgresErrorCodes.NumericValueOutOfRange
            })
        {
            // The caller disposes this scope before retrying; never reuse partially mutated entities.
            throw new CatalogBatchWriteException(exception);
        }
        logger.LogInformation("Catalog database batch saved. Candidates: {Candidates}, Added: {Added}, Updated: {Updated}, IndividualFallback: {Fallback}, ReadMs: {ReadMs}, PrepareMs: {PrepareMs}, SaveMs: {SaveMs}",
            observations.Count, added.Count, updated.Count, retry.Count, readMs, prepareMs, Stopwatch.GetElapsedTime(saving).TotalMilliseconds);
        return new(added.Count, updated.Count, skipped, retry);
    }

}
