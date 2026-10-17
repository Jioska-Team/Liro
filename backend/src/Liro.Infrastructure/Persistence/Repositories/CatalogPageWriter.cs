using System.Diagnostics;
using System.Text.Json;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Liro.Infrastructure.Persistence.Repositories;

public sealed class CatalogPageWriter(LiroDbContext db, CatalogRefreshPolicy policy,
    ILogger<ImportRobloxItemService> importLogger, ILogger<CatalogPageWriter> logger) : ICatalogPageWriter
{
    public async Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> observations, bool onlyNew, CancellationToken cancellationToken)
    {
        if (observations.Count > 120) throw new ArgumentOutOfRangeException(nameof(observations));
        if (observations.Count == 0) return new(0, 0, 0, []);
        foreach (var key in observations.Keys) key.Validate();
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
        var buffer = new PageBuffer(existing, failures);
        var importer = new ImportRobloxItemService(new UnusedCatalog(), buffer, new UnusedThumbnail(), importLogger, buffer, policy);
        var retry = new List<CatalogItemKey>();
        var skipped = 0;
        foreach (var (key, remote) in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await buffer.CanAttemptAsync(key, DateTime.UtcNow, cancellationToken)
                || (onlyNew && buffer.Existing.ContainsKey(key))) { skipped++; continue; }
            // Deletions have dependent records and use the existing individual transaction path.
            if (remote.Details?.CreatorId != 1 || !string.Equals(remote.Details.CreatorType, "User", StringComparison.OrdinalIgnoreCase))
            { retry.Add(key); continue; }
            try { await importer.ApplyWithOutcomeAsync(key, remote, onlyNew, cancellationToken, refreshThumbnail: false); }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                retry.Add(key);
                logger.LogWarning(exception, "Catalog observation {Key} requires individual processing.", key);
            }
        }
        var prepareMs = Stopwatch.GetElapsedTime(preparation).TotalMilliseconds;
        var saving = Stopwatch.GetTimestamp();
        foreach (var item in buffer.Added) db.Items.Add(item);
        foreach (var item in buffer.Updated) db.Items.Update(item);
        var resolved = failures.Where(x => buffer.Resolved.Contains(new(x.AssetId, x.ItemType))).ToArray();
        db.RemoveRange(resolved);
        try
        {
            // One SaveChanges: items, market states, snapshots, outbox and resolved retries
            // participate in EF's implicit transaction and existing execution strategy.
            if (buffer.Added.Count + buffer.Updated.Count + resolved.Length > 0)
                await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.StringDataRightTruncation or PostgresErrorCodes.NumericValueOutOfRange })
        {
            // The caller disposes this scope before retrying; never reuse partially mutated entities.
            throw new CatalogBatchWriteException(exception);
        }
        logger.LogInformation("Catalog database batch saved. Candidates: {Candidates}, Added: {Added}, Updated: {Updated}, IndividualFallback: {Fallback}, ReadMs: {ReadMs}, PrepareMs: {PrepareMs}, SaveMs: {SaveMs}",
            observations.Count, buffer.Added.Count, buffer.Updated.Count, retry.Count, readMs, prepareMs, Stopwatch.GetElapsedTime(saving).TotalMilliseconds);
        return new(buffer.Added.Count, buffer.Updated.Count, skipped, retry);
    }

    private sealed class PageBuffer(IEnumerable<Item> existing, IEnumerable<CatalogImportFailure> failures) : IItemRepository, ICatalogImportFailureStore
    {
        public Dictionary<CatalogItemKey, Item> Existing { get; } = existing.ToDictionary(x => new CatalogItemKey(x.RobloxAssetId, x.ItemType));
        private readonly Dictionary<CatalogItemKey, CatalogImportFailure> errors = failures.ToDictionary(x => new CatalogItemKey(x.AssetId, x.ItemType));
        public List<Item> Added { get; } = [];
        public List<Item> Updated { get; } = [];
        public HashSet<CatalogItemKey> Resolved { get; } = [];
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(Existing.GetValueOrDefault(key));
        public Task<Item?> GetByAssetIdAsync(long id, CancellationToken cancellationToken = default) => GetByCatalogIdAsync(new(id), cancellationToken);
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Existing.Values.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsByAssetIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Existing.ContainsKey(new(id)));
        public Task AddAsync(Item item, CancellationToken cancellationToken = default) { Added.Add(item); return Task.CompletedTask; }
        public Task UpdateAsync(Item item, CancellationToken cancellationToken = default) { Updated.Add(item); return Task.CompletedTask; }
        public Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken cancellationToken) =>
            Task.FromResult(!errors.TryGetValue(key, out var failure) || (failure.DeadLetteredAtUtc is null && failure.NextAttemptAtUtc <= now));
        public Task<bool> CanAttemptAsync(long id, DateTime now, CancellationToken cancellationToken) => CanAttemptAsync(new CatalogItemKey(id), now, cancellationToken);
        public Task ResolveAsync(CatalogItemKey key, CancellationToken cancellationToken) { Resolved.Add(key); return Task.CompletedTask; }
        public Task ResolveAsync(long id, CancellationToken cancellationToken) => ResolveAsync(new CatalogItemKey(id), cancellationToken);
        public Task DeleteByAssetIdAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeferRefreshAsync(long id, DateTime time, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> GetStaleAssetIdsAsync(DateTime now, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> GetDueAsync(DateTime now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordFailureAsync(long id, string error, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class UnusedCatalog : IRobloxCatalogClient
    {
        public Task<RobloxCatalogItem?> GetItemAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException("Page observations already contain details.");
    }
    private sealed class UnusedThumbnail : IRobloxThumbnailClient
    {
        public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException("Page writes do not request images.");
    }
}
