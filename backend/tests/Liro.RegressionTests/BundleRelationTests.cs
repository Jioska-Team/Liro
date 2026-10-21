using System.Net;
using System.Text.Json;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Roblox;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liro.RegressionTests;

internal static class BundleRelationTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Las relaciones eliminan duplicados y rechazan observaciones antiguas", () =>
        {
            var bundle = new Item(1, "Avatar", itemType: CatalogItemType.Bundle);
            var now = DateTime.UtcNow;
            bundle.UpdateBundleComponents([30, 20, 30], now);
            Check.That(bundle.BundleComponentAssetIds.SequenceEqual(new long[] { 20, 30 }), "Duplicados");
            Check.That(!bundle.UpdateBundleComponents([99], now.AddSeconds(-1)), "Observación antigua aceptada");
            Check.That(bundle.BundleComponentAssetIds.Length == 2, "Se perdieron componentes");
            bundle.UpdateBundleComponents([], now.AddSeconds(1));
            Check.That(bundle.BundleComponentAssetIds.Length == 0, "No se retiraron relaciones obsoletas");
            Check.Throws<ArgumentException>(() => new Item(1, "Pieza").UpdateBundleComponents([2], now));
        });
        suite.Test("El catálogo oculta cuerpos y conserva accesorios y paquetes", () =>
        {
            var visible = CatalogVisibility.Visible.Compile();
            foreach (var type in new[] { 17, 27, 28, 29, 30, 31, 79, 8, 18, 41, 76, 77 })
            {
                var item = new Item(type, "Artículo");
                item.UpdateDetails(new ItemCatalogDetails { AssetTypeId = type });
                var body = new[] { 17, 27, 28, 29, 30, 31, 79 }.Contains(type);
                Check.That(visible(item) == !body && CatalogVisibility.IsBodyPart(item) == body, $"Tipo incorrecto: {type}");
            }
            Check.That(visible(new Item(30, "Avatar", itemType: CatalogItemType.Bundle)), "Paquete oculto");
        });
        suite.AsyncTest("Roblox diferencia componentes de conjuntos de usuario y elimina repeticiones", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":1,"itemType":"Bundle","name":"Avatar","itemRestrictions":[],"bundledItems":[{"id":50,"type":"Asset"},{"id":50,"type":"Asset"},{"id":60,"type":"UserOutfit"}]}]}""") })) { BaseAddress = new("https://catalog.roblox.com/") };
            var item = await new RobloxCatalogClient(http).GetItemAsync(new CatalogItemKey(1, CatalogItemType.Bundle));
            Check.That(item!.BundleComponentAssetIds!.SequenceEqual(new long[] { 50 }), "Se mezclaron identidades");
        });
        suite.AsyncTest("Una respuesta sin composición no equivale a paquete vacío", async () =>
        {
            foreach (var field in new[] { "", ",\"bundledItems\":[]" })
            {
                using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":1,\"itemType\":\"Bundle\",\"name\":\"Avatar\",\"itemRestrictions\":[]" + field + "}]}") })) { BaseAddress = new("https://catalog.roblox.com/") };
                var item = await new RobloxCatalogClient(http).GetItemAsync(new CatalogItemKey(1, CatalogItemType.Bundle));
                Check.That((item!.BundleComponentAssetIds is null) == (field == ""), "Ausencia confundida con lista vacía");
            }
        });
        suite.Test("La base exige relaciones únicas y permite compartir componentes", () =>
        {
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
            var relation = db.Model.FindEntityType(typeof(BundleComponent))!;
            Check.That(relation.FindPrimaryKey()!.Properties.Select(x => x.Name).SequenceEqual(new[] { "BundleId", "ComponentItemId" }), "Clave no compuesta");
            Check.That(relation.GetForeignKeys().Count() == 2, "Faltan claves externas");
            Check.That(!relation.GetIndexes().Any(x => x.IsUnique), "Un componente no puede compartirse");
            Check.That(!db.Database.HasPendingModelChanges(), "Migración incompleta");
            var sql = db.Items.Where(CatalogVisibility.Visible).Take(24).ToQueryString();
            Check.That(sql.Contains("WHERE") && sql.Contains("LIMIT"), "El filtro no se aplica en SQL");
        });
    }
}
