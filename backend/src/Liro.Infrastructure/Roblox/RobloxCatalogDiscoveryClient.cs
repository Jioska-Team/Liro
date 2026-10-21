using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogDiscoveryClient(HttpClient httpClient, IOptions<RobloxCatalogOptions> options, ILogger<RobloxCatalogDiscoveryClient>? logger = null,
    RobloxCatalogSession? session = null) : IRobloxCatalogDiscoveryClient
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
        var url = $"v2/search/items/details?SortType={partition.SortType}&Limit={config.PageSize}&IncludeNotForSale=true&SalesTypeFilter={(int)partition.Scope}&sortOrder=Desc";
        if (partition.CategoryFilter is { } category)
            url += $"&CategoryFilter={category}";
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
        return session is null ? await DownloadPageAsync(url, partition, cursor, cancellationToken)
            : await session.ShareAsync("search:" + url, token => DownloadPageAsync(url, partition, cursor, token), cancellationToken);
    }
    private async Task<RobloxCatalogDiscoveryPage> DownloadPageAsync(string url, CatalogSearchPartition partition, string? cursor, CancellationToken cancellationToken)
    {
        var observedAt = DateTime.UtcNow;
        var downloadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        if (page?.Data is null)
        {
            throw new JsonException("Roblox catalog response is missing data.");
        }
        logger?.LogInformation("Catalog search page downloaded. Rows: {Rows}, DownloadMs: {DownloadMs}",
            page.Data.Count, System.Diagnostics.Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds);
        if (!string.IsNullOrWhiteSpace(page.NextPageCursor) && page.NextPageCursor == cursor)
        {
            throw new JsonException("Roblox returned a non-advancing catalog cursor.");
        }
        var items = new List<CatalogItemKey>();
        var bundledAssets = new HashSet<CatalogItemKey>();
        var limited = new List<CatalogItemKey>();
        var details = new Dictionary<CatalogItemKey, RobloxCatalogResult>();
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
            var identity = new CatalogItemKey(item.Id, parsed.Value);
            if (parsed == CatalogItemType.Bundle && item.CreatorTargetId == 1
                && string.Equals(item.CreatorType, "User", StringComparison.OrdinalIgnoreCase)
                && item.AdditionalFields?.TryGetValue("bundledItems", out var components) == true
                && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var component in components.EnumerateArray())
                {
                    // UserOutfit IDs are a different namespace. Never infer ownership from the bundle.
                    if (component.ValueKind != JsonValueKind.Object
                        || !component.TryGetProperty("type", out var componentType)
                        || componentType.ValueKind != JsonValueKind.String || componentType.GetString() != "Asset"
                        || !component.TryGetProperty("id", out var componentId)
                        || componentId.ValueKind != JsonValueKind.Number || !componentId.TryGetInt64(out var id) || id <= 0)
                        continue;
                    bundledAssets.Add(new(id, CatalogItemType.Asset));
                }
            }
            if (details.ContainsKey(identity))
            {
                details[identity] = new(null, "Duplicate search identity; details confirmation required.");
            }
            else
            {
                try
                {
                    var raw = JsonSerializer.SerializeToElement(item);
                    var mapped = RobloxCatalogClient.MapItem(raw, identity, observedAt) with
                    {
                        Source = "RobloxCatalogSearchV2"
                    };
                    if (mapped.Details?.CreatorId is null || mapped.Details.CreatorType is null
                        || mapped.Details.CreatedAtUtc is null
                        || (parsed == CatalogItemType.Asset ? mapped.Details.AssetTypeId : mapped.Details.BundleTypeId) is null)
                        throw new JsonException("Incomplete search metadata; details confirmation required.");
                    details[identity] = new(mapped, null);
                }
                catch (JsonException exception) { details[identity] = new(null, exception.Message); }
            }
            if (item.CreatorTargetId == 1 && string.Equals(item.CreatorType, "User", StringComparison.OrdinalIgnoreCase)
                && item.ItemRestrictions?.Any(x => x is "Limited" or "LimitedUnique") == true)
            {
                limited.Add(new CatalogItemKey(item.Id, parsed.Value));
            }
        }
        // Publish only fully validated observations after duplicate identities have been rejected.
        foreach (var observation in details.Values)
            if (observation.Error is null && observation.Item is { } valid)
                session?.Store(valid);
        var unique = items.Distinct().ToArray();
        var result = new RobloxCatalogDiscoveryPage(unique.Where(x => x.Type == CatalogItemType.Asset).Select(x => x.Id).ToArray(), page.NextPageCursor)
        {
            Items = unique,
            Details = details,
            LimitedItems = limited.Distinct().ToArray(),
            ReturnedCount = page.Data.Count,
            BundledAssets = bundledAssets.Except(unique).ToArray()
        };
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
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalFields
        {
            get; init;
        }
        [JsonPropertyName("creatorTargetId")]
        public long? CreatorTargetId
        {
            get; init;
        }
        [JsonPropertyName("creatorType")]
        public string? CreatorType
        {
            get; init;
        }
        [JsonPropertyName("itemRestrictions")]
        public string[]? ItemRestrictions
        {
            get; init;
        }
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


