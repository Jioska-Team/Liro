using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;

namespace Liro.Workers.Catalog;
// Persist failures independently of page cursors so a bad asset cannot block the whole catalog.
public sealed class CatalogImportProcessor(ImportRobloxItemService importer, IItemRepository repository, ICatalogImportFailureStore failures, ILogger<CatalogImportProcessor> logger)
{
    public async Task ProcessAsync(long assetId, bool skipFresh, CancellationToken cancellationToken)
    {
        if (!await failures.CanAttemptAsync(assetId, DateTime.UtcNow, cancellationToken))
        {
            return;
        }

        if (skipFresh)
        {
            var existing = await repository.GetByAssetIdAsync(assetId, cancellationToken);
            if (existing?.LastCheckedAt is not null && existing.NextRefreshAt > DateTime.UtcNow)
            {
                return;
            }
        }

        try
        {
            await importer.ImportAsync(assetId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw;
        }
        catch (Exception exception)
        {
            // If persisting the retry fails too, propagate it: the caller must not advance its cursor.
            await failures.RecordFailureAsync(assetId, exception.Message, DateTime.UtcNow, cancellationToken);
            await repository.DeferRefreshAsync(assetId, DateTime.UtcNow.AddMinutes(5), cancellationToken);
            logger.LogWarning(exception, "Asset {AssetId} import queued for independent retry; other items can continue.", assetId);
        }
    }
}
