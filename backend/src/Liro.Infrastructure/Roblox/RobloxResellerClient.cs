using System.Text.Json;
using Liro.Application.Catalog.Services;

namespace Liro.Infrastructure.Roblox;

// Shared by transient HTTP clients; bounds both retention and memory use.
public sealed class RobloxResellerCache(TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<string, ItemResellerPage> pages = [];
    public ItemResellerPage? Find(string key)
    {
        lock (sync)
        {
            if (!pages.TryGetValue(key, out var page)) return null;
            var age = clock.GetUtcNow().UtcDateTime - page.ObservedAtUtc!.Value;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(120)) return page;
            pages.Remove(key);
            return null;
        }
    }
    public void Store(string key, ItemResellerPage page)
    {
        lock (sync)
        {
            if (pages.Count >= 500 && !pages.ContainsKey(key))
                pages.Remove(pages.MinBy(x => x.Value.ObservedAtUtc).Key);
            pages[key] = page;
        }
    }
}

public sealed class RobloxResellerClient(HttpClient http, RobloxCatalogSession session,
    RobloxResellerCache cache, TimeProvider clock) : IRobloxResellerClient
{
    public Task<ItemResellerPage> GetAsync(Guid collectibleId, string? cursor, CancellationToken cancellationToken)
    {
        if (collectibleId == Guid.Empty || cursor is { Length: > 1024 } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid collectible or cursor.");
        var key = $"resellers:{collectibleId:D}:{cursor}";
        return session.ShareAsync(key, async ct =>
        {
            if (cache.Find(key) is { } cached) return cached;
            var url = $"marketplace-sales/v1/item/{collectibleId:D}/resellers?limit=10";
            if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var page = Parse(json.RootElement, clock.GetUtcNow().UtcDateTime);
            cache.Store(key, page);
            return page;
        }, cancellationToken);
    }

    public static ItemResellerPage Parse(JsonElement root, DateTime observedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 10)
            throw new JsonException("Resellers response must contain a bounded data array.");
        var offers = new List<ItemResellerOffer>();
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("collectibleItemInstanceId", out var instance)
                || instance.ValueKind != JsonValueKind.String || !Guid.TryParse(instance.GetString(), out var instanceId)
                || instanceId == Guid.Empty || !instances.Add(instanceId.ToString("D"))
                || Number(row, "price") is not { } price || price < 0)
                throw new JsonException("Reseller offer has no unique instance or valid price.");
            long? sellerId = null;
            string? sellerType = null, sellerName = null;
            if (row.TryGetProperty("seller", out var seller) && seller.ValueKind != JsonValueKind.Null)
            {
                if (seller.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid seller.");
                sellerId = Number(seller, "sellerId");
                if (sellerId <= 0) throw new JsonException("Invalid seller identity.");
                sellerType = Text(seller, "sellerType");
                sellerName = Text(seller, "name");
            }
            var serial = Number(row, "serialNumber");
            if (serial < 0) throw new JsonException("Invalid serial number.");
            offers.Add(new(instanceId.ToString("D"), sellerId, sellerType, sellerName, serial, price));
        }
        return new("Available", offers, Cursor(root, "nextPageCursor"), Cursor(root, "previousPageCursor"), observedAtUtc);
    }
    private static string? Cursor(JsonElement root, string key)
    {
        var value = Text(root, key);
        if (value is { Length: > 1024 } || value?.Any(char.IsControl) == true) throw new JsonException("Invalid provider cursor.");
        return string.IsNullOrEmpty(value) ? null : value;
    }
    private static string? Text(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Invalid string field.");
        return value.GetString();
    }
    private static long? Number(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw new JsonException("Invalid numeric field.");
        return number;
    }
}
