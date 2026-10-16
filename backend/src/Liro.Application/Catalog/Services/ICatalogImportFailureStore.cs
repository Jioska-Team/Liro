using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
namespace Liro.Application.Catalog.Services;

public interface ICatalogImportFailureStore
{
    Task<bool> CanAttemptAsync(long assetId, DateTime now, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetDueAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task RecordFailureAsync(long assetId, string error, DateTime now, CancellationToken cancellationToken);
    Task ResolveAsync(long assetId, CancellationToken cancellationToken);
    async Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken cancellationToken)
    {
        RequireAsset(key);
        var allowed = await CanAttemptAsync(key.Id, now, cancellationToken);
        return allowed;
    }
    async Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        var ids = await GetDueAsync(now, limit, cancellationToken);
        var items = ids.Select(id => new CatalogItemKey(id)).ToArray();
        return items;
    }
    async Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken cancellationToken)
    {
        RequireAsset(key);
        await RecordFailureAsync(key.Id, error, now, cancellationToken);
    }
    async Task ResolveAsync(CatalogItemKey key, CancellationToken cancellationToken)
    {
        RequireAsset(key);
        await ResolveAsync(key.Id, cancellationToken);
    }
    private static void RequireAsset(CatalogItemKey key)
    {
        key.Validate();
        if (key.Type != CatalogItemType.Asset)
        {
            throw new NotSupportedException("Typed failure store required for bundles.");
        }
    }
}

