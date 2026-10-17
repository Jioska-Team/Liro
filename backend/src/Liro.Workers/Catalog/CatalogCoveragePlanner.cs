using Liro.Application.RobloxIntegration.Models;
using Liro.Infrastructure.Roblox;
namespace Liro.Workers.Catalog;

public static class CatalogCoveragePlanner
{
    public static IReadOnlyList<CatalogSearchPartition> Roots(RobloxCatalogOptions options)
    {
        var roots = options.SearchScopes.Distinct().Select(scope => new CatalogSearchPartition(scope)).ToList();
        roots.AddRange(options.CoverageAssetTypeIds.Select(type => new CatalogSearchPartition(CatalogSearchScope.All) { AssetTypeId = type }));
        roots.AddRange(options.CoverageBundleTypeIds.Select(type => new CatalogSearchPartition(CatalogSearchScope.All) { BundleTypeId = type }));
        roots.AddRange(options.CoverageKeywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Select(keyword => new CatalogSearchPartition(CatalogSearchScope.All) { Keyword = keyword.Trim() }));
        if (options.CoverageAdditionalFiltersEnabled)
        {
            // These overlap with All; independent cursors let us measure their additional coverage.
            roots.Add(new(CatalogSearchScope.Premium));
            roots.Add(new(CatalogSearchScope.TimedOptions));
            foreach (var category in new[] { 1, 2, 5, 7 })
                roots.Add(new(CatalogSearchScope.All) { CategoryFilter = category });
            // No price bounds: off-sale items remain eligible even without a usable price.
            foreach (var sort in new[] { 2, 4, 5 })
                roots.Add(new(CatalogSearchScope.All) { SortType = sort });
        }
        var result = roots.DistinctBy(partition => partition.Key).OrderBy(partition => partition.Keyword is null ? 1 : 0).ToArray();
        return result;
    }
    public static IReadOnlyList<CatalogSearchPartition> Expand(CatalogSearchPartition parent, RobloxCatalogOptions options)
    {
        if (parent.Depth >= options.CoverageMaxDepth || parent.SortType != 0 || parent.Keyword is not null)
        {
            return [];
        }
        var children = new List<CatalogSearchPartition>();
        var next = parent with
        {
            Depth = parent.Depth + 1
        };
        if (parent.AssetTypeId is null && parent.BundleTypeId is null)
        {
            children.AddRange(options.CoverageAssetTypeIds.Select(type => next with { AssetTypeId = type }));
            children.AddRange(options.CoverageBundleTypeIds.Select(type => next with { BundleTypeId = type }));
        }
        else if (parent.MinPrice is null)
        {
            foreach (var range in new[] { (0, 0), (1, 100), (101, 1000), (1001, 10000), (10001, 100000), (100001, int.MaxValue) })
            {
                children.Add(next with
                {
                    MinPrice = range.Item1,
                    MaxPrice = range.Item2
                });
            }
        }
        else if (parent.MinPrice < parent.MaxPrice)
        {
            var middle = parent.MinPrice.Value + (parent.MaxPrice!.Value - parent.MinPrice.Value) / 2;
            children.Add(next with
            {
                MaxPrice = middle
            });
            children.Add(next with
            {
                MinPrice = middle + 1
            });
        }
        // Complementary orders improve discovery even when off-sale items lack usable prices.
        children.Add(next with
        {
            SortType = 3
        });
        children.Add(next with
        {
            SortType = 1
        });
        if (options.CoverageAdditionalFiltersEnabled)
        {
            foreach (var sort in new[] { 2, 4, 5 })
                children.Add(next with { SortType = sort });
        }
        return children;
    }
}

