using Liro.Application.Catalog.Services;

namespace Liro.RegressionTests;

sealed class MemoryFailureStore : ICatalogImportFailureStore
{
    private readonly HashSet<long> failed = [];
    public Task<bool> CanAttemptAsync(long id, DateTime now, CancellationToken ct) => Task.FromResult(!failed.Contains(id));
    public Task<IReadOnlyList<long>> GetDueAsync(DateTime now, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>(failed.ToArray());
    public Task RecordFailureAsync(long id, string error, DateTime now, CancellationToken ct)
    {
        failed.Add(id);
        return Task.CompletedTask;
    }

    public Task ResolveAsync(long id, CancellationToken ct)
    {
        failed.Remove(id);
        return Task.CompletedTask;
    }
}
