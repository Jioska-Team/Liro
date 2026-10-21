using Liro.Domain.Catalog.Models;
using Liro.Application.Catalog.Services;

namespace Liro.RegressionTests;

sealed class MemoryFailureStore : ICatalogImportFailureStore
{
    private readonly HashSet<CatalogItemKey> failed = [];
    public Task<bool> CanAttemptAsync(CatalogItemKey id, DateTime now, CancellationToken ct) => Task.FromResult(!failed.Contains(id));
    public Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogItemKey>>(failed.ToArray());
    public Task RecordFailureAsync(CatalogItemKey id, string error, DateTime now, CancellationToken ct)
    {
        failed.Add(id);
        return Task.CompletedTask;
    }

    public Task ResolveAsync(CatalogItemKey id, CancellationToken ct)
    {
        failed.Remove(id);
        return Task.CompletedTask;
    }
}
