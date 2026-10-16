using Liro.Application.RobloxIntegration.Models;
namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogOptions
{
    // Only the official Roblox user is accepted by configuration validation.
    public long? CreatorTargetId { get; set; } = 1;
    public string CreatorType { get; set; } = "User";
    public int Category { get; set; } = 1;
    public int PageSize { get; set; } = 120;
    public string? Taxonomy
    {
        get; set;
    }
    public CatalogSearchScope[] SearchScopes { get; set; } = [CatalogSearchScope.All, CatalogSearchScope.Collectibles];
    public string ScopeKey => $"catalog:v2:off-sale:typed:{CreatorType}:{CreatorTargetId}:{PageSize}:{Taxonomy ?? "all"}";
    public int[] CoverageAssetTypeIds { get; set; } = [8, 41, 42, 43, 44, 45, 46, 47, 17, 18, 19, 2, 11, 12, 27, 28, 29, 30, 31, 48, 50, 51, 52, 53, 54, 55, 61, 64, 65, 66, 67, 68, 69, 70, 71, 72, 76, 77, 78, 79];
    public int[] CoverageBundleTypeIds { get; set; } = [1, 2, 3, 4];
    public string[] CoverageKeywords { get; set; } = ["valkyrie"];
    public int CoverageMaxPartitions { get; set; } = 512;
    public int CoverageMaxDepth { get; set; } = 12;
    public int CoverageResultLimit { get; set; } = 1000;
    public string CoverageScopeKey => "catalog:coverage:v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ScopeKey)))[..16];
}




