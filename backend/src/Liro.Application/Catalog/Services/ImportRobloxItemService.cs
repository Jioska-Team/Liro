using Liro.Domain.Catalog.Models;
using System.Text.Json;
using Liro.Application.RobloxIntegration.Models;
using Liro.Application.Catalog.Repositories;
using Liro.Application.RobloxIntegration;
using Liro.Domain.Catalog.Entities;
using Microsoft.Extensions.Logging;

namespace Liro.Application.Catalog.Services;

public sealed class ImportRobloxItemService(IRobloxCatalogClient robloxCatalogClient, IItemRepository itemRepository, IRobloxThumbnailClient robloxThumbnailClient, ILogger<ImportRobloxItemService> logger, ICatalogImportFailureStore failures, CatalogRefreshPolicy refreshPolicy)
{
    public async Task<Item?> ImportAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        var remote = await robloxCatalogClient.GetItemAsync(key, cancellationToken);
        var result = await ApplyAsync(key, remote, false, cancellationToken);
        return result;
    }

    public async Task<Item?> ApplyAsync(CatalogItemKey key, RobloxCatalogItem? remote, bool onlyNew, CancellationToken cancellationToken = default)
    {
        var outcome = await ApplyWithOutcomeAsync(key, remote, onlyNew, cancellationToken);
        return outcome.Item;
    }
    public async Task<CatalogImportOutcome> ApplyWithOutcomeAsync(CatalogItemKey key, RobloxCatalogItem? remote, bool onlyNew, CancellationToken cancellationToken = default)
    {
        key.Validate();
        var existing = await itemRepository.GetByCatalogIdAsync(key, cancellationToken);
        if (onlyNew && existing is not null)
        {
            return new CatalogImportOutcome(existing, false);
        }
        if (remote is null)
        {
            if (existing is not null)
            {
                await itemRepository.DeferRefreshAsync(key, DateTime.UtcNow.AddHours(6), cancellationToken);
            }

            await failures.ResolveAsync(key, cancellationToken);
            return new CatalogImportOutcome(null, false);
        }

        if (remote.AssetId != key.Id || remote.ItemType != key.Type)
        {
            throw new JsonException("Roblox returned a different asset ID.");
        }

        var details = remote.Details;
        var knownOtherCreator = (details?.CreatorId is > 0 and not 1)
            || string.Equals(details?.CreatorType, "Group", StringComparison.OrdinalIgnoreCase);
        if (knownOtherCreator)
        {
            if (existing is not null)
            {
                await itemRepository.DeleteAsync(key, cancellationToken);
            }
            await failures.ResolveAsync(key, cancellationToken);
            logger.LogInformation("Excluded asset {AssetId}: creator is not the official Roblox user.", key);
            return new CatalogImportOutcome(null, false);
        }
        if (details?.CreatorId != 1 || !string.Equals(details.CreatorType, "User", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Catalog creator identity is missing; cannot confirm the official Roblox user.");
        }

        var item = existing ?? new Item(remote.AssetId, remote.Name, remote.CollectibleItemId, remote.MarketStatus, remote.ItemType);
        if (existing is not null)
        {
            item.UpdateCatalogData(remote.Name, remote.CollectibleItemId, remote.MarketStatus);
        }

        if (remote.Details is not null)
        {
            item.UpdateDetails(remote.Details);
        }

        if (remote.Market is not null)
        {
            item.ObserveMarket(remote.Market, remote.ObservedAtUtc, remote.Source);
        }

        try
        {
            item.UpdateThumbnail(await robloxThumbnailClient.GetThumbnailAsync(key, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Thumbnail unavailable for {AssetId}; retaining catalog data.", key);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Invalid thumbnail response for {AssetId}.", key);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Thumbnail timed out for {AssetId}.", key);
        }

        item.MarkChecked(DateTime.UtcNow, refreshPolicy.Interval);
        if (existing is null)
        {
            await itemRepository.AddAsync(item, cancellationToken);
        }
        else
        {
            await itemRepository.UpdateAsync(item, cancellationToken);
        }

        await failures.ResolveAsync(key, cancellationToken);
        return new CatalogImportOutcome(item, existing is null);
    }
    public async Task<Item?> ImportAsync(long assetId, CancellationToken cancellationToken = default)
    {
        var item = await ImportAsync(new CatalogItemKey(assetId), cancellationToken);
        return item;
    }
}



