using System.Net;
using System.Text.Json;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Enums;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxComponentClient(HttpClient http) : IRobloxComponentClient
{
    public async Task<RobloxCatalogItem?> GetAssetAsync(long assetId, CancellationToken ct)
    {
        if (assetId <= 0) throw new ArgumentOutOfRangeException(nameof(assetId));
        using var response = await http.GetAsync($"v2/assets/{assetId}/details", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Map(document.RootElement, assetId);
    }

    private static RobloxCatalogItem Map(JsonElement value, long assetId)
    {
        try
        {
            if (value.GetProperty("AssetId").GetInt64() != assetId) throw new JsonException("La identidad del componente no coincide.");
            var name = value.GetProperty("Name").GetString();
            var creator = value.GetProperty("Creator");
            var creatorId = creator.GetProperty("CreatorTargetId").GetInt64();
            var creatorType = creator.GetProperty("CreatorType").GetString();
            var assetType = value.GetProperty("AssetTypeId").GetInt32();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 || creatorId <= 0
                || creatorType is not ("User" or "Group") || assetType <= 0)
                throw new JsonException("Metadatos incompletos del componente.");
            var limited = value.GetProperty("IsLimited").GetBoolean();
            var unique = value.GetProperty("IsLimitedUnique").GetBoolean();
            var status = unique ? ItemMarketStatus.LimitedUnique : limited ? ItemMarketStatus.Limited : ItemMarketStatus.Normal;
            Guid? collectibleId = null;
            if (value.TryGetProperty("CollectibleItemId", out var collectible) && collectible.ValueKind != JsonValueKind.Null)
            {
                if (!Guid.TryParse(collectible.GetString(), out var parsed) || parsed == Guid.Empty)
                    throw new JsonException("Identificador coleccionable inválido.");
                collectibleId = parsed;
            }
            return new(assetId, name, collectibleId, status)
            {
                Source = "RobloxComponentDetails", ObservedAtUtc = DateTime.UtcNow,
                Details = new()
                {
                    CreatorId = creatorId, CreatorType = creatorType, CreatorName = creator.GetProperty("Name").GetString(),
                    Description = value.GetProperty("Description").GetString(), AssetTypeId = assetType,
                    CreatedAtUtc = value.GetProperty("Created").GetDateTime().ToUniversalTime(),
                    Restrictions = unique ? ["LimitedUnique"] : limited ? ["Limited"] : []
                }
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new JsonException("Respuesta inválida de detalles del componente.", ex); }
    }
}
