namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogOptions
{
    // Preserve the existing Roblox-created catalog scope by default. Null includes all creators.
    public long? CreatorTargetId { get; set; } = 1;
    public string CreatorType { get; set; } = "User";
    public int Category { get; set; } = 1;
    public int PageSize { get; set; } = 30;
    public string ScopeKey => $"catalog:{Category}:{CreatorType}:{CreatorTargetId}:{PageSize}";
}
