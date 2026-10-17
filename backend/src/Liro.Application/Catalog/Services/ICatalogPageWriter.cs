using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Services;

public sealed record CatalogPageWriteResult(int Added, int Updated, int Skipped, IReadOnlyList<CatalogItemKey> RetryIndividually);

public sealed class CatalogBatchWriteException(Exception inner) : Exception("Catalog batch could not be saved atomically; retry in fresh item scopes.", inner);

public interface ICatalogPageWriter
{
    Task<CatalogPageWriteResult> WriteAsync(IReadOnlyDictionary<CatalogItemKey, RobloxCatalogItem> observations, bool onlyNew, CancellationToken cancellationToken);
}
