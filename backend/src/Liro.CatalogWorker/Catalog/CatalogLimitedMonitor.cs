using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;

namespace Liro.CatalogWorker.Catalog;

public sealed class CatalogLimitedMonitor(IServiceScopeFactory scopes, IOptions<CatalogWorkerOptions> settings,
    IOptions<RobloxCatalogOptions> catalog, ILogger<CatalogLimitedMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.LimitedMonitorEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (RobloxRateLimitedException exception)
            {
                await CatalogWorkerDelay.WaitForRateLimitAsync(exception, settings.Value.RetryDelay, logger, stoppingToken);
                continue;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Limited monitor failed; checkpoint retained.");
                await Task.Delay(settings.Value.RetryDelay, stoppingToken);
            }
            // Successful passes immediately rejoin the shared gate and global budget.
        }
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        using var priority = RobloxRequestPriority.MainScan();
        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>();
        var queue = scope.ServiceProvider.GetRequiredService<ICatalogPriorityQueue>();
        if (settings.Value.BoundedRecentDiscovery && settings.Value.BoundedLimitedDiscovery)
        {
            var windowStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var window = await CatalogRecentWindow.ReadAsync(client, CatalogSearchScope.Collectibles, settings.Value.RecentLimitedLimit,
                page => ApplyPageAsync(page, queue, cancellationToken), cancellationToken);
            logger.LogInformation("Limited recent window completed. Limit: {Limit}, Pages: {Pages}, Observations: {Observations}, HitLimit: {HitLimit}, ElapsedSeconds: {Seconds}",
                settings.Value.RecentLimitedLimit, window.Pages, window.Observations, window.HitLimit,
                System.Diagnostics.Stopwatch.GetElapsedTime(windowStarted).TotalSeconds);
            return;
        }
        var checkpoints = scope.ServiceProvider.GetRequiredService<ICatalogCheckpointStore>();
        var key = catalog.Value.ScopeKey + ":limited-monitor:v1";
        var checkpoint = await checkpoints.LoadAsync(key, cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var head = await ReadPendingPageAsync(client, null, cancellationToken);
        await ApplyPageAsync(head, queue, cancellationToken);
        var lastHead = System.Diagnostics.Stopwatch.GetTimestamp();
        var pages = 1;
        var items = head.ReturnedCount;
        // Resume saved progress and drain the cursor chain. The shared HTTP gate supplies pacing.
        var cursor = checkpoint.Cursor ?? head.NextPageCursor;
        await checkpoints.SaveAsync(key, new(string.IsNullOrWhiteSpace(cursor) ? null : cursor, false), cancellationToken);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(cursor)) throw new System.Text.Json.JsonException("Limited search returned a cyclic cursor chain.");
            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastHead) >= settings.Value.LimitedPollInterval)
            {
                // A long scan must not delay newly published entries until the end of the pass.
                var newest = await ReadPendingPageAsync(client, null, cancellationToken);
                await ApplyPageAsync(newest, queue, cancellationToken);
                lastHead = System.Diagnostics.Stopwatch.GetTimestamp();
                pages++;
                items += newest.ReturnedCount;
            }
            RobloxCatalogDiscoveryPage tail;
            try { tail = await ReadPendingPageAsync(client, cursor, cancellationToken); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                await checkpoints.SaveAsync(key, new(null, false), cancellationToken);
                logger.LogWarning("Limited monitor cursor expired; head retained and backlog will restart.");
                return;
            }
            await ApplyPageAsync(tail, queue, cancellationToken);
            cursor = tail.NextPageCursor;
            // Commit each processed page, not only the final page of a long scan.
            await checkpoints.SaveAsync(key, new(string.IsNullOrWhiteSpace(cursor) ? null : cursor, false), cancellationToken);
            pages++;
            items += tail.ReturnedCount;
        }
        logger.LogInformation("Limited monitor polled. HeadCandidates: {Count}, HasBacklog: {Backlog}", head.LimitedItems.Count, cursor is not null);
        logger.LogInformation("Limited scan completed. Pages: {Pages}, Observations: {Items}, ElapsedSeconds: {Seconds}",
            pages, items, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    private Task<RobloxCatalogDiscoveryPage> ReadPendingPageAsync(IRobloxCatalogDiscoveryClient client, string? cursor, CancellationToken ct) =>
        CatalogWorkerDelay.ReadPendingPageAsync(token => client.GetPageAsync(CatalogSearchScope.Collectibles, true, cursor, token), logger, ct);

    private async Task ApplyPageAsync(RobloxCatalogDiscoveryPage page, ICatalogPriorityQueue queue, CancellationToken cancellationToken)
    {
        // Persist incomplete limited signals; complete observations need no second HTTP request.
        var fallback = page.LimitedItems.Where(key => !page.Details.TryGetValue(key, out var result)
            || result.Item is null || result.Error is not null).ToArray();
        await queue.EnqueueAsync(fallback, DateTime.UtcNow, cancellationToken);
        await CatalogSearchPageProcessor.ProcessAsync(scopes, page, false, settings.Value, cancellationToken, fetchFallback: false);
    }
}
