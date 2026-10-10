namespace Liro.Infrastructure.Persistence;

public sealed class CatalogCheckpointState
{
    public string Scope { get; set; } = "";
    public string? Cursor
    {
        get; set;
    }
    public bool Completed
    {
        get; set;
    }
    public DateTime UpdatedAtUtc
    {
        get; set;
    }
}
