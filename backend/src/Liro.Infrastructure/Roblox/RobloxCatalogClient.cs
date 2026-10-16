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
    public async Task<RobloxCatalogItem?> GetItemAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        var observedAtUtc = DateTime.UtcNow;
        using var response = await GetDetailsResponseAsync([key], cancellationToken);
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

        var result = MapItem(data[0], key, observedAtUtc);
        return result;
    }

    public async Task<RobloxCatalogItem?> GetItemAsync(long assetId, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(new CatalogItemKey(assetId), cancellationToken);
        return item;
    }
    public async Task<IReadOnlyDictionary<CatalogItemKey, RobloxCatalogResult>> GetItemsAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken cancellationToken = default)
    {
        var unique = keys.Distinct().ToArray();
        if (unique.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(keys));
        }
        foreach (var key in unique)
        {
            key.Validate();
        }
        var results = unique.ToDictionary(key => key, _ => new RobloxCatalogResult(null, "Item omitted from catalog response."));
        if (unique.Length == 0)
        {
            return results;
        }
        var observedAt = DateTime.UtcNow;
        using var response = await GetDetailsResponseAsync(unique, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest && unique.Length > 1)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var errors = JsonDocument.Parse(errorBody);
            if (errors.RootElement.TryGetProperty("errors", out var entries) && entries.ValueKind == JsonValueKind.Array
                && entries.EnumerateArray().Any(error => error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 2))
            {
                // Roblox documents Invalid count but not a fixed maximum. Split only that error.
                foreach (var part in unique.Chunk((unique.Length + 1) / 2))
                {
                    foreach (var pair in await GetItemsAsync(part, cancellationToken))
                    {
                        results[pair.Key] = pair.Value;
                    }
                }
                return results;
            }
        }
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Catalog response must contain a data array.");
        }
        var seen = new HashSet<CatalogItemKey>();
        foreach (var entry in data.EnumerateArray())
        {
            var id = ReadNumber(entry, "id");
            var type = ReadEnum(entry, "itemType", new Dictionary<int, string> { [1] = "Asset", [2] = "Bundle" });
            if (id is null || !Enum.TryParse<CatalogItemType>(type, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new JsonException("Catalog result has no valid typed identity.");
            }
            var key = new CatalogItemKey(id.Value, parsed);
            if (!results.ContainsKey(key))
            {
                throw new JsonException("Catalog returned an unrequested identity.");
            }
            if (!seen.Add(key))
            {
                results[key] = new(null, "Duplicate catalog result.");
                continue;
            }
            try
            {
                results[key] = new(MapItem(entry, key, observedAt), null);
            }
            catch (JsonException exception) { results[key] = new(null, exception.Message); }
        }
        return results;
    }
    private async Task<HttpResponseMessage> GetDetailsResponseAsync(IReadOnlyList<CatalogItemKey> keys, CancellationToken cancellationToken)
    {
        var response = await SendDetailsAsync(keys, null, cancellationToken);
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
        var retryResponse = await SendDetailsAsync(keys, token, cancellationToken);
        return retryResponse;
    }

    private async Task<HttpResponseMessage> SendDetailsAsync(IReadOnlyList<CatalogItemKey> keys, string? csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/catalog/items/details")
        {
            Content = JsonContent.Create(new { items = keys.Select(key => new { itemType = (int)key.Type, id = key.Id }).ToArray() })
        };
        if (csrfToken is not null)
        {
            request.Headers.Add("x-csrf-token", csrfToken);
        }

        var response = await httpClient.SendAsync(request, cancellationToken);
        return response;
    }
    private static RobloxCatalogItem MapItem(JsonElement item, CatalogItemKey key, DateTime observedAtUtc)
    {
        try
        {
            var name = ReadString(item, "name");
            if (ReadNumber(item, "id") != key.Id || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200
                || ReadEnum(item, "itemType", new Dictionary<int, string> { [1] = "Asset", [2] = "Bundle" }) != key.Type.ToString()
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
            var result = new RobloxCatalogItem(key.Id, name, collectibleId, status)
            {
                ItemType = key.Type,
                Details = new ItemCatalogDetails
                {
                    Description = ReadString(item, "description"),
                    CreatorId = ReadNumber(item, "creatorTargetId"),
                    CreatorType = ReadEnum(item, "creatorType", new Dictionary<int, string> { [1] = "User", [2] = "Group" }),
                    CreatorName = ReadString(item, "creatorName"),
                    AssetTypeId = key.Type == CatalogItemType.Asset ? checked((int?)ReadNumber(item, "assetType")) : null,
                    BundleTypeId = key.Type == CatalogItemType.Bundle ? checked((int?)ReadNumber(item, "bundleType")) : null,
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




