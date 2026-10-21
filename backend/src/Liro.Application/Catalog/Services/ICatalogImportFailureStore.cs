using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Services;

public interface ICatalogImportFailureStore
{
    Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken cancellationToken);
    Task ResolveAsync(CatalogItemKey key, CancellationToken cancellationToken);
}
