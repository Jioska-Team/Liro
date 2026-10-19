using System.Diagnostics;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;

namespace Liro.CatalogWorker.Catalog;

public sealed class CatalogKnownLimitedWorker(IServiceScopeFactory scopes, IOptions<CatalogWorkerOptions> settings,
    ILogger<CatalogKnownLimitedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.Value.KnownLimitedMonitorEnabled) return;
        var nextSeed = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextSeed)
                {
                    using var scope = scopes.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<ICatalogLimitedWatchlist>();
                    await store.SeedKnownAsync(DateTime.UtcNow, ct);
                    var health = await store.GetHealthAsync(DateTime.UtcNow, ct);
                    logger.LogInformation("Known limited watchlist. Total: {Total}, Due: {Due}, Failures: {Failures}, OldestConfirmationUtc: {Oldest}",
                        health.Total, health.Pending, health.Failed, health.OldestConfirmedAtUtc);
                    logger.LogInformation("Consultas compartidas acumuladas en este proceso: {SharedTotal}. El contador se reinicia al arrancar y cuenta consumidores unidos, no respuestas correctas ni solicitudes HTTP ahorradas.",
                        scope.ServiceProvider.GetService<RobloxCatalogSession>()?.SharedQueryCount ?? 0);
                    nextSeed = DateTime.UtcNow.AddMinutes(1);
                }
                if (await PollAsync(ct) == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RobloxRateLimitedException ex) { await CatalogWorkerDelay.WaitForRateLimitAsync(ex, settings.Value.RetryDelay, logger, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Known limited batch failed; watchlist retained.");
                await Task.Delay(settings.Value.RetryDelay, ct);
            }
        }
    }

    public async Task<int> PollAsync(CancellationToken ct)
    {
        using var operation = RobloxRequestPriority.Operation("limitados conocidos");
        var options = settings.Value;
        using var priority = new RobloxRequestPriority();
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICatalogLimitedWatchlist>();
        var entries = (await store.ClaimAsync(DateTime.UtcNow, options.KnownLimitedBatchSize, TimeSpan.FromMinutes(5), ct)).ToList();
        if (entries.Count == 0) return 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var session = scope.ServiceProvider.GetService<RobloxCatalogSession>();
            var results = new Dictionary<Liro.Domain.Catalog.Models.CatalogItemKey, RobloxCatalogResult>();
            var freshness = options.KnownLimitedInterval < TimeSpan.FromMinutes(1) ? options.KnownLimitedInterval : TimeSpan.FromMinutes(1);
            var inspected = 0;
            // Cached observations do not occupy an HTTP batch slot. Fill those slots from
            // the next oldest due IDs, bounded to avoid monopolizing database work.
            for (var claims = 0; claims < 10; claims++)
            {
                foreach (var entry in entries.Skip(inspected))
                    if (session?.FindRecent(entry.Key, freshness) is { } recent)
                        results[entry.Key] = new(recent, null);
                inspected = entries.Count;
                var slots = options.KnownLimitedBatchSize - (entries.Count - results.Count);
                if (slots <= 0 || claims == 9) break;
                var more = await store.ClaimAsync(DateTime.UtcNow, slots, TimeSpan.FromMinutes(5), ct);
                if (more.Count == 0) break;
                entries.AddRange(more);
            }
            // A search may have completed during the extra claims. Recheck before building
            // the HTTP batch, without extending any observation's original timestamp.
            if (session is not null)
                foreach (var entry in entries.Where(x => !results.ContainsKey(x.Key)))
                    if (session.FindRecent(entry.Key, freshness) is { } recent)
                        results[entry.Key] = new(recent, null);
            var reused = results.Count;
            var missing = entries.Select(x => x.Key).Where(key => !results.ContainsKey(key)).ToArray();
            if (missing.Length > 0)
                foreach (var pair in await scope.ServiceProvider.GetRequiredService<IRobloxCatalogClient>().GetItemsAsync(missing, ct))
                    results[pair.Key] = pair.Value;
            var valid = entries.Where(x => results.TryGetValue(x.Key, out var r) && r.Item is not null && r.Error is null).ToArray();
            var page = new RobloxCatalogDiscoveryPage([], null)
            {
                Items = valid.Select(x => x.Key).ToArray(),
                Details = results
            };
            var outcome = await CatalogSearchPageProcessor.ProcessAsync(scopes, page, false, options, ct, fetchFallback: false);
            var now = DateTime.UtcNow;
            // Each observation retains its own age. An old cached item must not make the
            // newly fetched items immediately due again or jump ahead of untouched IDs.
            if (outcome.Failed == 0)
                await store.CompleteObservedAsync(valid, valid.ToDictionary(x => x.Key, x => results[x.Key].Item!.ObservedAtUtc), options.KnownLimitedInterval, ct);
            else
                await store.CompleteAsync(valid, now, now.Add(options.RetryDelay), false, ct);
            if (reused > 0)
                logger.LogInformation("Revisión de limitados conocidos: {Reused} observaciones recientes reutilizadas; {Requested} identificadores requieren consulta.", reused, missing.Length);
            var invalid = entries.Except(valid).ToArray();
            foreach (var entry in invalid)
            {
                using var failureScope = scopes.CreateScope();
                var result = results.GetValueOrDefault(entry.Key) ?? new RobloxCatalogResult(null, "Known limited omitted from details response.");
                await failureScope.ServiceProvider.GetRequiredService<CatalogImportProcessor>()
                    .ProcessResultAsync(entry.Key, result, false, ct, refreshThumbnail: false);
            }
            await store.CompleteAsync(invalid, DateTime.UtcNow, DateTime.UtcNow.Add(options.RetryDelay), false, ct);
            logger.LogInformation("Known limited batch completed. Requested: {Count}, Confirmed: {Confirmed}, Failed: {Failed}, ElapsedSeconds: {Seconds}",
                entries.Count, outcome.Failed == 0 ? valid.Length : 0, invalid.Length + outcome.Failed, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return entries.Count;
        }
        catch (RobloxRateLimitedException exception)
        {
            // Release the claimed batch at the provider deadline, not the generic one-minute retry.
            if (!ct.IsCancellationRequested)
                await store.CompleteAsync(entries, DateTime.UtcNow, exception.RetryAtUtc.UtcDateTime, false, ct);
            throw;
        }
        catch
        {
            if (!ct.IsCancellationRequested)
                await store.CompleteAsync(entries, DateTime.UtcNow, DateTime.UtcNow.Add(options.RetryDelay), false, ct);
            // Cancellation leaves the lease to expire instead of dropping work.
            throw;
        }
    }
}
