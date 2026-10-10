using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Microsoft.Extensions.Options;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogDiscoveryClient(HttpClient httpClient, IOptions<RobloxCatalogOptions> options) : IRobloxCatalogDiscoveryClient
{
    public Task<RobloxCatalogDiscoveryPage> GetCatalogPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => GetPageAsync(0, cursor, cancellationToken);
    public Task<RobloxCatalogDiscoveryPage> GetRecentlyUpdatedPageAsync(string? cursor = null, CancellationToken cancellationToken = default) => GetPageAsync(3, cursor, cancellationToken);
    private async Task<RobloxCatalogDiscoveryPage> GetPageAsync(int sort, string? cursor, CancellationToken cancellationToken)
    {
        var config = options.Value;
        var url = $"v1/search/items/details?Category={config.Category}&SortType={sort}&Limit={config.PageSize}";
        if (config.CreatorTargetId is { } creator)
        {
            url += $"&CreatorTargetId={creator}&CreatorType={Uri.EscapeDataString(config.CreatorType)}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&Cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await httpClient.GetAsync(url, cancellationToken);
        // Preserve StatusCode for cursor recovery and retry policy; never log a full remote body.
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        if (page?.Data is null)
        {
            throw new JsonException("Roblox catalog response is missing data.");
        }

        if (page.NextPageCursor is not null && page.NextPageCursor == cursor)
        {
            throw new JsonException("Roblox returned a non-advancing catalog cursor.");
        }

        var ids = page.Data.Where(x => x.Id > 0 && string.Equals(x.ItemType, "Asset", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).Distinct().ToArray();
        return new RobloxCatalogDiscoveryPage(ids, page.NextPageCursor);
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
        [JsonPropertyName("id")]
        public long Id
        {
            get; init;
        }

        [JsonPropertyName("itemType")]
        public string? ItemType
        {
            get; init;
        }
    }
}
