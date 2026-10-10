namespace Liro.Domain.Catalog.Models;

public sealed record ItemCatalogDetails
{
    public string? Description
    {
        get; init;
    }
    public long? CreatorId
    {
        get; init;
    }
    public string? CreatorType
    {
        get; init;
    }
    public string? CreatorName
    {
        get; init;
    }
    public int? AssetTypeId
    {
        get; init;
    }
    public string[] Restrictions { get; init; } = [];
    public DateTime? CreatedAtUtc
    {
        get; init;
    }
    public DateTime? UpdatedAtUtc
    {
        get; init;
    }
}
