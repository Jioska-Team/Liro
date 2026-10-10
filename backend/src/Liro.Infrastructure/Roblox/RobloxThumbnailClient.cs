using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liro.Application.RobloxIntegration;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxThumbnailClient(HttpClient httpClient) : IRobloxThumbnailClient
{
    public async Task<string?> GetAssetThumbnailAsync(long assetId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<ThumbnailResponse>($"v1/assets?assetIds={assetId}&size=420x420&format=WebP&isCircular=false", cancellationToken);
            var thumbnail = response?.Data?.FirstOrDefault(x => x.TargetId == assetId);
            if (thumbnail is null || !string.Equals(thumbnail.State, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (thumbnail.ImageUrl is null || thumbnail.ImageUrl.Length > 1000 || !Uri.TryCreate(thumbnail.ImageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new JsonException("Invalid Roblox thumbnail URL.");
            }

            return thumbnail.ImageUrl;
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException exception)
        {
            throw new HttpRequestException("Thumbnail circuit is open.", exception);
        }
        catch (Polly.Timeout.TimeoutRejectedException exception)
        {
            throw new HttpRequestException("Thumbnail request timed out.", exception);
        }
    }

    private sealed class ThumbnailResponse
    {
        [JsonPropertyName("data")]
        public List<ThumbnailItem>? Data
        {
            get; init;
        }
    }

    private sealed class ThumbnailItem
    {
        [JsonPropertyName("targetId")]
        public long TargetId
        {
            get; init;
        }

        [JsonPropertyName("state")]
        public string? State
        {
            get; init;
        }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl
        {
            get; init;
        }
    }
}
