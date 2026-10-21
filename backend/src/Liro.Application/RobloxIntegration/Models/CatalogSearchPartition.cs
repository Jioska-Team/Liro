using System.Security.Cryptography;
using System.Text;
namespace Liro.Application.RobloxIntegration.Models;

public sealed record CatalogSearchPartition(CatalogSearchScope Scope)
{
    public int? CategoryFilter
    {
        get; init;
    }
    public int? AssetTypeId
    {
        get; init;
    }
    public int? BundleTypeId
    {
        get; init;
    }
    public int? MinPrice
    {
        get; init;
    }
    public int? MaxPrice
    {
        get; init;
    }
    public int SortType
    {
        get; init;
    }
    public string? Keyword
    {
        get; init;
    }
    public int Depth
    {
        get; init;
    }
    public string Key => $"{Scope}:a{AssetTypeId}:b{BundleTypeId}:p{MinPrice}-{MaxPrice}:s{SortType}:k{(Keyword is null ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Keyword)))[..16])}"
        + (CategoryFilter is { } category ? $":category{category}" : "")
        // Replay only bundle partitions once to discover components of already imported bundles.
        + (BundleTypeId.HasValue ? ":components-v1" : "");
    public void Validate()
    {
        if (!Enum.IsDefined(Scope) || AssetTypeId is <= 0 || BundleTypeId is <= 0 || (AssetTypeId.HasValue && BundleTypeId.HasValue)
            || MinPrice is < 0 || MaxPrice is < 0 || MinPrice > MaxPrice || SortType is < 0 or > 5 || Keyword?.Length > 80
            || CategoryFilter is < 0 or > 7)
        {
            throw new ArgumentException("Invalid catalog partition.");
        }
    }
}
