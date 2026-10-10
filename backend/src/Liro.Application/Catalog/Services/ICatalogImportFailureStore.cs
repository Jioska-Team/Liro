namespace Liro.Application.Catalog.Services;

public interface ICatalogImportFailureStore
{
    Task<bool> CanAttemptAsync(long assetId, DateTime now, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetDueAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task RecordFailureAsync(long assetId, string error, DateTime now, CancellationToken cancellationToken);
    Task ResolveAsync(long assetId, CancellationToken cancellationToken);
}
