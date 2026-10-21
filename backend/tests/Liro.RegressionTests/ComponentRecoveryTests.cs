using System.Net;
using System.Text.Json;
using Liro.Infrastructure.Roblox;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Entities;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class ComponentRecoveryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("El arranque conserva componentes externos oficiales y elimina artículos ajenos sin relación", () =>
        {
            Item Make(long id, long creator, CatalogItemType type = CatalogItemType.Asset)
            {
                var item = new Item(id, "Prueba", itemType: type);
                item.UpdateDetails(new()
                {
                    CreatorId = creator,
                    CreatorType = "User"
                });
                return item;
            }
            var official = Make(1, 1, CatalogItemType.Bundle);
            official.UpdateBundleComponents([10], DateTime.UtcNow);
            var linked = Make(10, 99);
            var unlinked = Make(11, 99);
            var foreignBundle = Make(2, 99, CatalogItemType.Bundle);
            foreignBundle.UpdateBundleComponents([11], DateTime.UtcNow);
            var collision = Make(10, 99, CatalogItemType.Bundle);
            Item[] items = [official, linked, unlinked, foreignBundle, collision];
            var deleted = CatalogOwnershipQueries.ForeignItemsWithoutOfficialBundle(items.AsQueryable()).ToArray();
            Check.That(deleted.Select(x => x.Id).ToHashSet().SetEquals(new[] { unlinked.Id, foreignBundle.Id, collision.Id }), "La limpieza borra componentes válidos o conserva artículos sin autorización");
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
            var sql = CatalogOwnershipQueries.ForeignItemsWithoutOfficialBundle(db.Items).ToQueryString();
            Check.That(sql.Contains("EXISTS") && sql.Contains("BundleComponentAssetIds"), "La protección no se traduce a PostgreSQL");
        });
        const string valid = """{"AssetId":10,"Name":"Cabeza","Description":"Pieza","AssetTypeId":17,"Creator":{"CreatorTargetId":1,"CreatorType":"User","Name":"Roblox"},"Created":"2021-01-01T00:00:00Z","IsLimited":false,"IsLimitedUnique":false,"CollectibleItemId":"5ac950bb-bc08-4425-9746-8e455c538654"}""";
        suite.AsyncTest("Los detalles alternativos conservan identidad y metadatos sin inventar precios", async () =>
        {
            using var http = Client(valid);
            var item = await new RobloxComponentClient(http).GetAssetAsync(10, default);
            Check.That(item is { AssetId: 10, ItemType: CatalogItemType.Asset, Market: null } && item.Details!.AssetTypeId == 17
                && item.Details.CreatorId == 1 && item.CollectibleItemId == Guid.Parse("5ac950bb-bc08-4425-9746-8e455c538654"), "Metadatos incorrectos");
        });
        suite.AsyncTest("La recuperación rechaza otra identidad y no fabrica artículos inexistentes", async () =>
        {
            using var http = Client(valid);
            await Check.ThrowsAsync<JsonException>(() => new RobloxComponentClient(http).GetAssetAsync(11, default));
            using var missing = Client("{}", HttpStatusCode.BadRequest);
            Check.That(await new RobloxComponentClient(missing).GetAssetAsync(10, default) is null, "Artículo inventado");
        });
        suite.AsyncTest("La recuperación respeta límites de solicitudes y rechaza creador desconocido", async () =>
        {
            using var limited = Client("{}", HttpStatusCode.TooManyRequests);
            await Check.ThrowsAsync<HttpRequestException>(() => new RobloxComponentClient(limited).GetAssetAsync(10, default));
            using var invalid = Client(valid.Replace("\"CreatorTargetId\":1", "\"CreatorTargetId\":0"));
            await Check.ThrowsAsync<JsonException>(() => new RobloxComponentClient(invalid).GetAssetAsync(10, default));
        });
    }
    private static HttpClient Client(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHttp(_ => new(status) { Content = new StringContent(body) }))
        {
            BaseAddress = new("https://economy.roblox.com/")
        };
}
