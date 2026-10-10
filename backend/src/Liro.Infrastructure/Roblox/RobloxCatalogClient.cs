using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxCatalogClient(HttpClient httpClient) : IRobloxCatalogClient
{
    public async Task<RobloxCatalogItem?> GetItemAsync(long assetId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(assetId);
        var observedAtUtc = DateTime.UtcNow;
        using var response = await GetDetailsResponseAsync(assetId, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Catalog response must contain a data array.");
        }

        if (data.GetArrayLength() == 0)
        {
            return null;
        }

        if (data.GetArrayLength() != 1)
        {
            throw new JsonException("Expected exactly one catalog item.");
        }

        var result = MapItem(data[0], assetId, observedAtUtc);
        return result;
    }

    private async Task<HttpResponseMessage> GetDetailsResponseAsync(long assetId, CancellationToken cancellationToken)
    {
        var response = await SendDetailsAsync(assetId, null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Forbidden
            || !response.Headers.TryGetValues("x-csrf-token", out var tokens))
        {
            return response;
        }

        var token = tokens.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return response;
        }

        // Roblox challenges this public POST even without authentication. Retry only once,
        // through the same HTTP pipeline so pacing, cooldowns and cancellation still apply.
        response.Dispose();
        var retryResponse = await SendDetailsAsync(assetId, token, cancellationToken);
        return retryResponse;
    }

    private async Task<HttpResponseMessage> SendDetailsAsync(long assetId, string? csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/catalog/items/details")
        {
            Content = JsonContent.Create(new { items = new[] { new { itemType = 1, id = assetId } } })
        };
        if (csrfToken is not null)
        {
            request.Headers.Add("x-csrf-token", csrfToken);
        }

        var response = await httpClient.SendAsync(request, cancellationToken);
        return response;
    }
    private static RobloxCatalogItem MapItem(JsonElement item, long assetId, DateTime observedAtUtc)
    {
        try
        {
            var name = ReadString(item, "name");
            if (ReadNumber(item, "id") != assetId || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200
                || ReadEnum(item, "itemType", new Dictionary<int, string> { [1] = "Asset", [2] = "Bundle" }) != "Asset"
                || !item.TryGetProperty("itemRestrictions", out var restrictionValues) || restrictionValues.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Invalid Roblox catalog item response.");
            }

            var restrictions = restrictionValues.EnumerateArray().Select(ReadRestriction).ToArray();
            var isLimited = restrictions.Contains("Limited", StringComparer.OrdinalIgnoreCase)
                || restrictions.Contains("LimitedUnique", StringComparer.OrdinalIgnoreCase);
            var collectibleText = ReadString(item, "collectibleItemId");
            Guid? collectibleId = null;
            if (collectibleText is not null)
            {
                if (!Guid.TryParse(collectibleText, out var parsed) || parsed == Guid.Empty)
                {
                    throw new JsonException("Invalid collectible item ID.");
                }

                collectibleId = parsed;
            }

            var hasResellers = ReadBoolean(item, "hasResellers");
            var market = new ItemMarketData
            {
                PrimaryPriceRobux = ReadNumber(item, "price"),
                LowestPriceRobux = ReadNumber(item, "lowestPrice"),
                LowestResalePriceRobux = ReadNumber(item, "lowestResalePrice"),
                PriceBeforeDiscountRobux = item.TryGetProperty("discountInformation", out var discount) && discount.ValueKind == JsonValueKind.Object
                    ? ReadNumber(discount, "originalPrice") : null,
                IsOffSale = ReadBoolean(item, "isOffSale"),
                HasResellers = hasResellers,
                IsResellable = isLimited || hasResellers.HasValue ? true : null,
                PriceStatus = ReadString(item, "priceStatus"),
                TotalQuantity = ReadNumber(item, "totalQuantity"),
                UnitsAvailableForConsumption = ReadNumber(item, "unitsAvailableForConsumption"),
                FavoriteCount = ReadNumber(item, "favoriteCount")
            };
            market.Validate();
            var status = restrictions.Contains("LimitedUnique", StringComparer.OrdinalIgnoreCase) ? ItemMarketStatus.LimitedUnique
                : isLimited ? ItemMarketStatus.Limited
                : restrictions.Contains("Collectible", StringComparer.OrdinalIgnoreCase) ? ItemMarketStatus.Collectible : ItemMarketStatus.Normal;
            var result = new RobloxCatalogItem(assetId, name, collectibleId, status)
            {
                Details = new ItemCatalogDetails
                {
                    Description = ReadString(item, "description"),
                    CreatorId = ReadNumber(item, "creatorTargetId"),
                    CreatorType = ReadEnum(item, "creatorType", new Dictionary<int, string> { [1] = "User", [2] = "Group" }),
                    CreatorName = ReadString(item, "creatorName"),
                    AssetTypeId = checked((int?)ReadNumber(item, "assetType")),
                    Restrictions = restrictions,
                    CreatedAtUtc = ReadDate(item, "itemCreatedUtc")
                },
                Market = market,
                ObservedAtUtc = observedAtUtc
            };
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw new JsonException("Invalid catalog field value.", exception);
        }
    }
    private static string? ReadString(JsonElement item, string name)
    {
        var value = item.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;
        return value;
    }

    private static string ReadRestriction(JsonElement value)
    {
        var restriction = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetInt32() switch
        {
            1 => "ThirteenPlus",
            2 => "LimitedUnique",
            3 => "Limited",
            4 => "BuildersClub",
            5 => "TurboBuildersClub",
            6 => "OutrageousBuildersClub",
            7 => "Rthro",
            8 => "Live",
            9 => "Collectible",
            var unknown => $"Unknown:{unknown}"
        };
        if (string.IsNullOrWhiteSpace(restriction))
        {
            throw new JsonException("Empty catalog restriction.");
        }

        return restriction;
    }

    private static string? ReadEnum(JsonElement item, string name, IReadOnlyDictionary<int, string> values)
    {
        if (!item.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.String ? property.GetString()
            : values.GetValueOrDefault(property.GetInt32(), $"Unknown:{property.GetInt32()}");
        return value;
    }

    private static long? ReadNumber(JsonElement item, string name)
    {
        var value = item.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetInt64() : (long?)null;
        return value;
    }

    private static bool? ReadBoolean(JsonElement item, string name)
    {
        var value = item.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetBoolean() : (bool?)null;
        return value;
    }

    private static DateTime? ReadDate(JsonElement item, string name)
    {
        var value = item.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetDateTimeOffset().UtcDateTime : (DateTime?)null;
        return value;
    }
}
