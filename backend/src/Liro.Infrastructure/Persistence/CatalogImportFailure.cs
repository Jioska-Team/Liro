namespace Liro.Infrastructure.Persistence;

public sealed class CatalogImportFailure
{
    public long AssetId
    {
        get; set;
    }
    public int Attempts
    {
        get; set;
    }
    public DateTime NextAttemptAtUtc
    {
        get; set;
    }
    public DateTime? DeadLetteredAtUtc
    {
        get; set;
    }
    public string Error { get; set; } = "";
}
