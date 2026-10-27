using System.Globalization;
using System.Text.Json;
using Liro.Application.Catalog.Services;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxOwnerCache(TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<string, ItemOwnerPage> pages = [];
    public ItemOwnerPage? Find(string key)
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
    public void Store(string key, ItemOwnerPage page)
    {
        lock (sync)
        {
            if (pages.Count >= 500 && !pages.ContainsKey(key))
                pages.Remove(pages.MinBy(x => x.Value.ObservedAtUtc).Key);
            pages[key] = page;
        }
    }
}
public sealed class RobloxOwnerClient(HttpClient http, RobloxCatalogSession session,
    RobloxOwnerCache cache, TimeProvider clock) : IRobloxOwnerClient
{
    public Task<ItemOwnerPage> GetAsync(long assetId, string? cursor, CancellationToken cancellationToken)
    {
        if (assetId <= 0 || cursor is { Length: > 1024 } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid asset or cursor.");
        var key = $"owners:{assetId.ToString(CultureInfo.InvariantCulture)}:{cursor}";
        return session.ShareAsync(key, async ct =>
        {
            if (cache.Find(key) is { } cached) return cached;
            var url = $"v2/assets/{assetId.ToString(CultureInfo.InvariantCulture)}/owners?sortOrder=Asc&limit=10";
            if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var page = Parse(json.RootElement, clock.GetUtcNow().UtcDateTime);
            cache.Store(key, page);
            return page;
        }, cancellationToken);
    }
    public static ItemOwnerPage Parse(JsonElement root, DateTime observedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 10)
            throw new JsonException("Owners response must contain a bounded data array.");
        var copies = new List<ItemOwnedCopy>();
        var uaids = new HashSet<long>();
        var instances = new HashSet<Guid>();
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || Number(row, "id") is not { } uaid
                || uaid <= 0 || !uaids.Add(uaid)) throw new JsonException("Invalid or duplicate copy identity.");
            var instance = Text(row, "collectibleItemInstanceId");
            if (instance is not null && (!Guid.TryParse(instance, out var instanceId) || instanceId == Guid.Empty || !instances.Add(instanceId)))
                throw new JsonException("Invalid or duplicate instance identity.");
            long? ownerId = null;
            string? ownerType = null, ownerName = null;
            if (row.TryGetProperty("owner", out var owner) && owner.ValueKind != JsonValueKind.Null)
            {
                if (owner.ValueKind != JsonValueKind.Object || Number(owner, "id") is not { } id || id <= 0)
                    throw new JsonException("Invalid owner identity.");
                ownerId = id;
                ownerType = OwnerType(owner);
                ownerName = Text(owner, "name");
            }
            var serial = Number(row, "serialNumber");
            if (serial < 0) throw new JsonException("Invalid serial number.");
            copies.Add(new(uaid, instance, serial, ownerId, ownerType, ownerName));
        }
        return new("Available", copies, Cursor(root, "nextPageCursor"), Cursor(root, "previousPageCursor"), observedAtUtc);
    }
    private static string? OwnerType(JsonElement owner)
    {
        if (!owner.TryGetProperty("type", out var type) || type.ValueKind == JsonValueKind.Null) return null;
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number))
            return number switch { 1 => "User", 2 => "Group", _ => null };
        if (type.ValueKind == JsonValueKind.String)
            return type.GetString() switch { "User" => "User", "Group" => "Group", _ => null };
        throw new JsonException("Invalid owner type.");
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
