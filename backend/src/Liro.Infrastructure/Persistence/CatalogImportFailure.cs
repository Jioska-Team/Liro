using Liro.Domain.Catalog.Enums;
namespace Liro.Infrastructure.Persistence;

public sealed class CatalogImportFailure
{
    public CatalogItemType ItemType { get; set; } = CatalogItemType.Asset;
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

