using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.Extensions.Options;

namespace Liro.CatalogWorker.Catalog;

public sealed class BundleComponentsWorker(IServiceScopeFactory scopes, IOptions<CatalogWorkerOptions> settings,
    ILogger<BundleComponentsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RobloxRateLimitedException ex) { await CatalogWorkerDelay.WaitForRateLimitAsync(ex, settings.Value.RetryDelay, logger, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudieron actualizar las relaciones de paquetes; se reintentara."); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    public async Task PollAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICatalogBrowseRepository>();
        await repository.SynchronizeRelationsAsync(ct);
        foreach (var key in await repository.GetBundlesMissingThumbnailAsync(ct))
        {
            var url = await scope.ServiceProvider.GetRequiredService<IRobloxThumbnailClient>().GetThumbnailAsync(key, ct);
            await repository.SaveBundleThumbnailAsync(key.Id, url, DateTime.UtcNow, ct);
        }
        var keys = await repository.GetBundlesDueAsync(DateTime.UtcNow.AddDays(-1), 100, ct);
        // Alternative details are individual HTTP requests; bound their impact on other monitors.
        if (keys.Count == 0) keys = await repository.GetMissingComponentsAsync(10, ct);
        if (keys.Count == 0) return;
        var results = (await scope.ServiceProvider.GetRequiredService<IRobloxCatalogClient>().GetItemsAsync(keys, ct)).ToDictionary(x => x.Key, x => x.Value);
        var valid = new List<CatalogItemKey>();
        foreach (var key in keys)
        {
            var result = results.GetValueOrDefault(key);
            if (key.Type == CatalogItemType.Asset && (result?.Item is null || result.Error is not null))
            {
                try
                {
                    var alternative = await scope.ServiceProvider.GetRequiredService<IRobloxComponentClient>().GetAssetAsync(key.Id, ct);
                    result = new(alternative, alternative is null ? "El componente no está disponible en catálogo ni en detalles de Roblox." : null);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is HttpRequestException { StatusCode: not System.Net.HttpStatusCode.TooManyRequests })
                { result = new(null, ex.Message); }
                results[key] = result;
            }
            var item = result?.Item;
            var official = item?.Details?.CreatorId == 1 && item.Details.CreatorType == "User";
            var referencedComponent = key.Type == CatalogItemType.Asset && item?.Details?.CreatorId > 0
                && item.Details.CreatorType is "User" or "Group"
                && await scope.ServiceProvider.GetRequiredService<IItemRepository>().IsOfficialBundleComponentAsync(key, ct);
            if (result?.Error is null && item is not null && (official || referencedComponent)
                && (key.Type != CatalogItemType.Bundle || item.BundleComponentAssetIds is not null))
            { valid.Add(key); continue; }
            await scope.ServiceProvider.GetRequiredService<ICatalogImportFailureStore>().RecordFailureAsync(key,
                result?.Error ?? "No se pudieron confirmar los componentes o el creador oficial.", DateTime.UtcNow, ct);
        }
        await CatalogSearchPageProcessor.ProcessAsync(scopes, new RobloxCatalogDiscoveryPage([], null)
            { Items = valid, Details = results }, false, settings.Value, ct, fetchFallback: false);
        await repository.SynchronizeRelationsAsync(ct);
        logger.LogInformation("Relaciones de paquetes actualizadas. Consultados: {Total}, Confirmados: {Valid}", keys.Count, valid.Count);
    }
}
