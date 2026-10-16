namespace Liro.Application.Catalog.Services;

public sealed record CatalogCheckpoint(string? Cursor, bool Completed)
{
    public long ItemsSeen
    {
        get; init;
    }
    public int PagesProcessed
    {
        get; init;
    }
    public long ItemsAdded
    {
        get; init;
    }
    public long ItemFailures
    {
        get; init;
    }
    public string CoverageStatus { get; init; } = "Unassessed";
    public string? LastError
    {
        get; init;
    }
}
public interface ICatalogCheckpointStore
{
    Task<CatalogCheckpoint> LoadAsync(string scope, CancellationToken cancellationToken);
    Task SaveAsync(string scope, CatalogCheckpoint checkpoint, CancellationToken cancellationToken);
}

