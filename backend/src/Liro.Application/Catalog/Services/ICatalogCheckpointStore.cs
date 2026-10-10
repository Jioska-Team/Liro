namespace Liro.Application.Catalog.Services;

public sealed record CatalogCheckpoint(string? Cursor, bool Completed);
public interface ICatalogCheckpointStore
{
    Task<CatalogCheckpoint> LoadAsync(string scope, CancellationToken cancellationToken);
    Task SaveAsync(string scope, CatalogCheckpoint checkpoint, CancellationToken cancellationToken);
}
