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
    public long ItemsSeen
    {
        get; set;
    }
    public int PagesProcessed
    {
        get; set;
    }
    public long ItemsAdded
    {
        get; set;
    }
    public long ItemFailures
    {
        get; set;
    }
    public string CoverageStatus { get; set; } = "Unassessed";
    public string? LastError
    {
        get; set;
    }
}

