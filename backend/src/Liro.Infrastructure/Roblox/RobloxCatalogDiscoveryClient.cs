using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.Extensions.Options;
namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogDiscoveryClient(HttpClient httpClient, IOptions<RobloxCatalogOptions> options) : IRobloxCatalogDiscoveryClient
{
    public async Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(CatalogSearchScope.All, false, cursor, cancellationToken);
        return page;
    }
    public async Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(CatalogSearchScope.All, true, cursor, cancellationToken);
        return page;
    }
    public async Task<RobloxCatalogDiscoveryPage> GetPageAsync(CatalogSearchScope scope, bool recent, string? cursor, CancellationToken cancellationToken)
    {
        var page = await GetPartitionPageAsync(new CatalogSearchPartition(scope) { SortType = recent ? 3 : 0 }, cursor, cancellationToken);
        return page;
    }
    public async Task<RobloxCatalogDiscoveryPage> GetPartitionPageAsync(CatalogSearchPartition partition, string? cursor, CancellationToken cancellationToken)
    {
        partition.Validate();
        var config = options.Value;
        var url = $"v2/search/items/details?SortType={partition.SortType}&Limit={config.PageSize}&IncludeNotForSale=true&SalesTypeFilter={(int)partition.Scope}";
        if (partition.AssetTypeId is { } asset)
        {
            url += $"&AssetTypeIds={asset}";
        }
        if (partition.BundleTypeId is { } bundle)
        {
            url += $"&BundleTypeIds={bundle}";
        }
        if (partition.MinPrice is { } min)
        {
            url += $"&MinPrice={min}";
        }
        if (partition.MaxPrice is { } max)
        {
            url += $"&MaxPrice={max}";
        }
        if (partition.Keyword is { } keyword)
        {
            url += "&Keyword=" + Uri.EscapeDataString(keyword);
        }
        if (config.CreatorTargetId is { } creator)
        {
            url += $"&CreatorTargetId={creator}&CreatorType={Uri.EscapeDataString(config.CreatorType)}";
        }
        if (!string.IsNullOrWhiteSpace(config.Taxonomy))
        {
            url += $"&Taxonomy={Uri.EscapeDataString(config.Taxonomy)}";
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&Cursor={Uri.EscapeDataString(cursor)}";
        }
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        if (page?.Data is null)
        {
            throw new JsonException("Roblox catalog response is missing data.");
        }
        if (!string.IsNullOrWhiteSpace(page.NextPageCursor) && page.NextPageCursor == cursor)
        {
            throw new JsonException("Roblox returned a non-advancing catalog cursor.");
        }
        var items = new List<CatalogItemKey>();
        foreach (var item in page.Data)
        {
            if (item.Id <= 0)
            {
                throw new JsonException("Invalid catalog identity.");
            }
            var type = item.ItemType.ValueKind == JsonValueKind.Number ? item.ItemType.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : item.ItemType.ValueKind == JsonValueKind.String ? item.ItemType.GetString() : null;
            var parsed = type switch
            {
                "Asset" or "1" => CatalogItemType.Asset,
                "Bundle" or "2" => CatalogItemType.Bundle,
                _ => (CatalogItemType?)null
            };
            if (parsed is null)
            {
                throw new JsonException("Unsupported catalog item type; cursor was not advanced.");
            }
            if ((partition.AssetTypeId is { } expectedAsset && (parsed != CatalogItemType.Asset || item.AssetType != expectedAsset))
                || (partition.BundleTypeId is { } expectedBundle && (parsed != CatalogItemType.Bundle || item.BundleType != expectedBundle)))
            {
                throw new JsonException("Roblox did not honor the requested catalog type partition.");
            }
            items.Add(new CatalogItemKey(item.Id, parsed.Value));
        }
        var unique = items.Distinct().ToArray();
        var result = new RobloxCatalogDiscoveryPage(unique.Where(x => x.Type == CatalogItemType.Asset).Select(x => x.Id).ToArray(), page.NextPageCursor) { Items = unique, ReturnedCount = page.Data.Count };
        return result;
    }
    private sealed class SearchResponse
    {
        [JsonPropertyName("nextPageCursor")]
        public string? NextPageCursor
        {
            get; init;
        }
        [JsonPropertyName("data")]
        public List<SearchItem>? Data
        {
            get; init;
        }
    }
    private sealed class SearchItem
    {
        [JsonPropertyName("assetType")]
        public int? AssetType
        {
            get; init;
        }
        [JsonPropertyName("bundleType")]
        public int? BundleType
        {
            get; init;
        }
        [JsonPropertyName("id")]
        public long Id
        {
            get; init;
        }
        [JsonPropertyName("itemType")]
        public JsonElement ItemType
        {
            get; init;
        }
    }
}


