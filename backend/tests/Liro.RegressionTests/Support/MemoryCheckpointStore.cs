using Liro.Application.Catalog.Services;

namespace Liro.RegressionTests;

sealed class MemoryCheckpointStore : ICatalogCheckpointStore
{
    private CatalogCheckpoint value = new(null, false);
    public Task<CatalogCheckpoint> LoadAsync(string key, CancellationToken ct) => Task.FromResult(value);
    public Task SaveAsync(string key, CatalogCheckpoint state, CancellationToken ct)
    {
        value = state;
        return Task.CompletedTask;
    }
}
