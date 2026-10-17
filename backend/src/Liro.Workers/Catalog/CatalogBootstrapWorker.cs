using System.Net;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;

namespace Liro.Workers.Catalog;

public sealed class CatalogBootstrapWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogBootstrapWorker> logger, IOptions<CatalogWorkerOptions> workerOptions, IOptions<RobloxCatalogOptions> catalogOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        if (!options.BootstrapEnabled)
        {
            return;
        }
        var config = catalogOptions.Value;
        var partitions = CatalogCoveragePlanner.Roots(config).ToDictionary(partition => partition.Key);
        var finished = new HashSet<string>();
        var pending = new LinkedList<CatalogSearchPartition>(partitions.Values.OrderBy(p => p.Scope == CatalogSearchScope.All ? 0 : 1));
        while (!stoppingToken.IsCancellationRequested && pending.First is not null)
        {
            var partition = pending.First.Value;
            pending.RemoveFirst();
            // One page per turn; retry/continuation goes to the tail. New All subdivisions
            // go to the head instead of waiting behind dozens of empty limited categories.
            if (finished.Contains(partition.Key))
            {
                continue;
            }

            var key = config.CoverageScopeKey + ":" + partition.Key;
            using var scope = scopeFactory.CreateScope();
            var checkpoints = scope.ServiceProvider.GetRequiredService<ICatalogCheckpointStore>();
            CatalogCheckpoint? checkpoint = null;
            try
            {
                checkpoint = await checkpoints.LoadAsync(key, stoppingToken);
                if (!checkpoint.Completed)
                {
                    var discovery = scope.ServiceProvider.GetRequiredService<IRobloxCatalogDiscoveryClient>();
                    RobloxCatalogDiscoveryPage page;
                    try
                    {
                        page = await discovery.GetPartitionPageAsync(partition, checkpoint.Cursor, stoppingToken);
                    }
                    catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest && checkpoint.Cursor is not null)
                    {
                        await checkpoints.SaveAsync(key, new(null, false)
                        {
                            CoverageStatus = "CursorReset",
                            LastError = "Provider rejected the saved cursor; restarting this partition."
                        }, stoppingToken);
                        await Task.Delay(options.RetryDelay, stoppingToken);
                        continue;
                    }
                    var outcome = await CatalogSearchPageProcessor.ProcessAsync(scopeFactory, page, true, options, stoppingToken);
                    var completed = string.IsNullOrWhiteSpace(page.NextPageCursor);
                    var seen = checkpoint.ItemsSeen + page.ReturnedCount;
                    var updated = checkpoint with
                    {
                        Cursor = completed ? null : page.NextPageCursor,
                        Completed = completed,
                        ItemsSeen = seen,
                        PagesProcessed = checkpoint.PagesProcessed + 1,
                        ItemsAdded = checkpoint.ItemsAdded + outcome.Added,
                        ItemFailures = checkpoint.ItemFailures + outcome.Failed,
                        CoverageStatus = !completed ? "Running" : seen >= config.CoverageResultLimit ? "PossiblyTruncated" : "SearchExhausted",
                        LastError = null
                    };
                    await checkpoints.SaveAsync(key, updated, stoppingToken);
                    checkpoint = updated;
                    logger.LogInformation("Coverage partition {Partition}: seen {Seen}, added {Added}, failures {Failures}, status {Status}",
                        partition.Key, checkpoint.ItemsSeen, checkpoint.ItemsAdded, checkpoint.ItemFailures, checkpoint.CoverageStatus);
                    await Task.Delay(options.PageDelay, stoppingToken);
                }
                if (checkpoint.Completed)
                {
                    finished.Add(partition.Key);
                    if (checkpoint.CoverageStatus == "PossiblyTruncated")
                    {
                        var children = CatalogCoveragePlanner.Expand(partition, config);
                        var scheduled = new List<CatalogSearchPartition>();
                        foreach (var child in children)
                        {
                            if (partitions.ContainsKey(child.Key)) { continue; }
                            if (partitions.Count >= config.CoverageMaxPartitions)
                            {
                                logger.LogWarning("Coverage partition budget reached ({Limit}); unresolved coverage remains visible as PossiblyTruncated.", config.CoverageMaxPartitions);
                                break;
                            }
                            if (partitions.TryAdd(child.Key, child)) { scheduled.Add(child); }
                        }
                        foreach (var child in scheduled.AsEnumerable().Reverse())
                        {
                            if (child.Scope == CatalogSearchScope.All) { pending.AddFirst(child); }
                            else { pending.AddLast(child); }
                        }
                        logger.LogWarning("Coverage partition {Partition} may be truncated. Complementary partitions: {Children}. Full coverage is unverified.", partition.Key, children.Count);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (RobloxRateLimitedException exception)
            {
                await CatalogWorkerDelay.WaitForRateLimitAsync(exception, options.RetryDelay, logger, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Coverage partition {Partition} failed; its checkpoint is retained.", partition.Key);
                if (checkpoint is not null)
                {
                    // Never record a failed page as completed or move its cursor.
                    try
                    {
                        var error = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
                        await checkpoints.SaveAsync(key, checkpoint with
                        {
                            CoverageStatus = "Failed",
                            LastError = error
                        }, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception saveError) { logger.LogWarning(saveError, "Could not persist partition failure status."); }
                }
                await Task.Delay(options.RetryDelay, stoppingToken);
            }
            finally
            {
                if (!finished.Contains(partition.Key)) { pending.AddLast(partition); }
            }
        }
    }
}


