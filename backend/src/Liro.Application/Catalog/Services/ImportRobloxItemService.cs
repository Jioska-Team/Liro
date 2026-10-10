using System.Text.Json;
using Liro.Application.Catalog.Repositories;
using Liro.Application.RobloxIntegration;
using Liro.Domain.Catalog.Entities;
using Microsoft.Extensions.Logging;

namespace Liro.Application.Catalog.Services;

public sealed class ImportRobloxItemService(IRobloxCatalogClient robloxCatalogClient, IItemRepository itemRepository, IRobloxThumbnailClient robloxThumbnailClient, ILogger<ImportRobloxItemService> logger, ICatalogImportFailureStore failures, CatalogRefreshPolicy refreshPolicy)
{
    public async Task<Item?> ImportAsync(long assetId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(assetId);
        var existing = await itemRepository.GetByAssetIdAsync(assetId, cancellationToken);
        var remote = await robloxCatalogClient.GetItemAsync(assetId, cancellationToken);
        if (remote is null)
        {
            if (existing is not null)
            {
                await itemRepository.DeferRefreshAsync(assetId, DateTime.UtcNow.AddHours(6), cancellationToken);
            }

            await failures.ResolveAsync(assetId, cancellationToken);
            return null;
        }

        if (remote.AssetId != assetId)
        {
            throw new JsonException("Roblox returned a different asset ID.");
        }

        var item = existing ?? new Item(remote.AssetId, remote.Name, remote.CollectibleItemId, remote.MarketStatus);
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
            item.UpdateThumbnail(await robloxThumbnailClient.GetAssetThumbnailAsync(assetId, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Thumbnail unavailable for {AssetId}; retaining catalog data.", assetId);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Invalid thumbnail response for {AssetId}.", assetId);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Thumbnail timed out for {AssetId}.", assetId);
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

        await failures.ResolveAsync(assetId, cancellationToken);
        return item;
    }
}
