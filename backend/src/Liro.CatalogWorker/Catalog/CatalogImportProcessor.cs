using System.Text.Json;
using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration.Models;

namespace Liro.CatalogWorker.Catalog;

// Persist item failures before callers advance their page checkpoint.
public sealed class CatalogImportProcessor(ImportRobloxItemService importer, IItemRepository repository, ICatalogImportFailureStore failures, ILogger<CatalogImportProcessor> logger)
{
    public async Task<bool> CanProcessAsync(CatalogItemKey key, bool onlyNew, CancellationToken cancellationToken)
    {
        key.Validate();
        if (onlyNew && await repository.GetByCatalogIdAsync(key, cancellationToken) is not null)
        {
            return false;
        }
        var allowed = await failures.CanAttemptAsync(key, DateTime.UtcNow, cancellationToken);
        return allowed;
    }

    public async Task<bool> ProcessAsync(CatalogItemKey key, bool onlyNew, CancellationToken cancellationToken)
    {
        if (!await CanProcessAsync(key, onlyNew, cancellationToken))
        {
            return false;
        }
        await PersistAsync(key, () => importer.ImportAsync(key, cancellationToken), cancellationToken);
        return true;
    }

    public async Task<(bool Added, bool Failed)> ProcessResultAsync(CatalogItemKey key, RobloxCatalogResult result, bool onlyNew, CancellationToken cancellationToken, bool refreshThumbnail = true)
    {
        if (!await CanProcessAsync(key, onlyNew, cancellationToken))
        {
            return (false, false);
        }
        var added = false;
        var succeeded = await PersistAsync(key, async () =>
        {
            if (result.Error is not null || result.Item is null)
            {
                throw new JsonException(result.Error ?? "Item omitted from catalog response.");
            }
            var outcome = await importer.ApplyWithOutcomeAsync(key, result.Item, onlyNew, cancellationToken, refreshThumbnail);
            added = outcome.Added;
            return outcome.Item;
        }, cancellationToken);
        return (added, !succeeded);
    }
    private async Task<bool> PersistAsync(CatalogItemKey key, Func<Task<Item?>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests) { throw; }
        catch (Exception exception)
        {
            await failures.RecordFailureAsync(key, exception.Message, DateTime.UtcNow, cancellationToken);
            await repository.DeferRefreshAsync(key, DateTime.UtcNow.AddMinutes(5), cancellationToken);
            logger.LogWarning(exception, "Catalog item {Key} queued for independent retry.", key);
            return false;
        }
    }

    public async Task<bool> ProcessAsync(long assetId, bool onlyNew, CancellationToken cancellationToken)
    {
        var attempted = await ProcessAsync(new CatalogItemKey(assetId), onlyNew, cancellationToken);
        return attempted;
    }
}

