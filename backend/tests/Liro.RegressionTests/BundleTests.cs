using System.Net;
using System.Text.Json;
using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Migrations;
using Liro.Infrastructure.Roblox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class BundleTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("ItemService preserves asset compatibility when a bundle has the same ID", async () =>
        {
            var repository = new TypedRepository();
            var bundle = new Item(126, "Bundle", itemType: CatalogItemType.Bundle);
            await repository.AddAsync(bundle);
            var service = new ItemService(repository);

            Check.That(await service.GetByAssetIdAsync(126) is null, "An asset lookup returned a bundle with the same ID");
            Check.That(ReferenceEquals(await service.GetByBundleIdAsync(126), bundle), "Bundle lookup lost its type");
            var asset = await service.CreateAsync(126, "Asset", null, ItemMarketStatus.Normal);
            Check.That(asset is not null && asset.ItemType == CatalogItemType.Asset, "A bundle prevented creation of the asset with the same ID");
            Check.That(ReferenceEquals(await service.GetByAssetIdAsync(126), asset), "Legacy lookup did not select the asset");
            Check.That(ReferenceEquals(await service.GetByBundleIdAsync(126), bundle), "Asset creation replaced the bundle");
            Check.That(await service.CreateAsync(126, "Duplicate", null, ItemMarketStatus.Normal) is null && repository.Items.Count == 2,
                "Existing asset was duplicated or the bundle was removed");
        });
        suite.AsyncTest("Un componente externo requiere una referencia de paquete oficial y conserva su creador", async () =>
        {
            foreach (var referenced in new[] { false, true })
            {
                var repository = new TypedRepository { OfficialComponent = referenced };
                using var http = Http(_ => throw new Exception("No se debe consultar el catálogo"));
                var importer = new ImportRobloxItemService(new RobloxCatalogClient(http), repository, new TypedThumbnail(), NullLogger<ImportRobloxItemService>.Instance, new TypedFailures(), new CatalogRefreshPolicy());
                var remote = new RobloxCatalogItem(16744204409, "Bold Climb", null, ItemMarketStatus.Normal)
                {
                    Details = new() { CreatorId = 5184673798, CreatorType = "User", AssetTypeId = 48 },
                    ObservedAtUtc = DateTime.UtcNow
                };
                var result = await importer.ApplyWithOutcomeAsync(new(16744204409), remote, false, default, refreshThumbnail: false);
                Check.That((result.Item is not null) == referenced, "Se aceptó un creador externo sin referencia o se descartó un componente confirmado");
                if (referenced)
                    Check.That(result.Item!.CreatorId == 5184673798 && repository.Items.Count == 1, "Se alteró el creador o se duplicó el componente");
            }
        });
        suite.Test("Bundle identity preserves asset URL and exposes distinct API identity", () =>
        {
            var asset = new Item(126, "Asset");
            var bundle = new Item(126, "Bundle", itemType: CatalogItemType.Bundle);
            var response = ItemResponse.From(bundle);
            Check.That(asset.MarketplaceUrl == "https://www.roblox.com/catalog/126" && bundle.MarketplaceUrl == "https://www.roblox.com/bundles/126", "Wrong marketplace resource");
            Check.That(response.ItemType == "Bundle" && response.RobloxItemId == 126 && response.RobloxAssetId is null, "Ambiguous bundle API identity");
            Check.That(ItemResponse.From(asset).RobloxAssetId == 126 && asset.Id != bundle.Id, "Asset contract changed");
            Check.Throws<ArgumentOutOfRangeException>(() => new Item(1, "Bad", itemType: (CatalogItemType)0));
        });
        suite.AsyncTest("Bundle import is idempotent and does not replace asset with same numeric ID", async () =>
        {
            var repository = new TypedRepository();
            var asset = new Item(126, "Existing asset");
            await repository.AddAsync(asset);
            var failures = new TypedFailures();
            failures.Keys.Add(new CatalogItemKey(126));
            failures.Keys.Add(new CatalogItemKey(126, CatalogItemType.Bundle));
            using var http = Http(request =>
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var sent = body.RootElement.GetProperty("items")[0];
                Check.That(sent.GetProperty("itemType").GetInt32() == 2 && sent.GetProperty("id").GetInt64() == 126, "Requested asset instead of bundle");
                return Json("""{"data":[{"id":126,"itemType":"Bundle","name":"Bundle","itemRestrictions":[],"creatorTargetId":1,"creatorType":"User","bundleType":1,"itemCreatedUtc":"2026-01-01T00:00:00Z"}]}""");
            });
            var thumbnails = new TypedThumbnail();
            var importer = new ImportRobloxItemService(new RobloxCatalogClient(http), repository, thumbnails, NullLogger<ImportRobloxItemService>.Instance, failures, new CatalogRefreshPolicy());
            var key = new CatalogItemKey(126, CatalogItemType.Bundle);
            var first = await importer.ImportAsync(key);
            var second = await importer.ImportAsync(key);
            Check.That(first is not null && second?.Id == first.Id && repository.Items.Count == 2, "Repeated bundle import duplicated or replaced data");
            Check.That(repository.Items[new CatalogItemKey(126)].Id == asset.Id && asset.Name == "Existing asset", "Asset was modified");
            Check.That(first!.ItemType == CatalogItemType.Bundle && first.BundleTypeId == 1 && first.AssetTypeId is null && first.LastCheckedAt is not null, "Incomplete bundle metadata");
            Check.That(failures.Keys.SetEquals([new CatalogItemKey(126)]) && thumbnails.Last == key, "Retry resolution or thumbnail crossed item types");
        });
        foreach (var creator in new[] { (1, "Group"), (99, "User") })
        {
            suite.AsyncTest($"Bundle import excludes nonofficial creator {creator}", async () =>
            {
                var repository = new TypedRepository();
                var asset = new Item(126, "Asset");
                await repository.AddAsync(asset);
                await repository.AddAsync(new Item(126, "Foreign bundle", itemType: CatalogItemType.Bundle));
                using var http = Http(_ => Json(JsonSerializer.Serialize(new { data = new[] { new { id = 126, itemType = "Bundle", name = "Foreign", itemRestrictions = Array.Empty<string>(), creatorTargetId = creator.Item1, creatorType = creator.Item2 } } })));
                var importer = new ImportRobloxItemService(new RobloxCatalogClient(http), repository, new TypedThumbnail(), NullLogger<ImportRobloxItemService>.Instance, new TypedFailures(), new CatalogRefreshPolicy());
                var result = await importer.ImportAsync(new CatalogItemKey(126, CatalogItemType.Bundle));
                Check.That(result is null && repository.Items.Count == 1 && repository.Items.Values.Single().Id == asset.Id, "Foreign bundle deletion touched asset");
            });
        }
        suite.AsyncTest("Bundle details reject mismatched type even when ID matches", async () =>
        {
            using var http = Http(_ => Json("""{"data":[{"id":126,"itemType":"Asset","name":"Wrong","itemRestrictions":[]}]}"""));
            await Check.ThrowsAsync<JsonException>(() => new RobloxCatalogClient(http).GetItemAsync(new CatalogItemKey(126, CatalogItemType.Bundle)));
        });
        suite.AsyncTest("Bundle thumbnails use bundle endpoint and target ID", async () =>
        {
            using var http = Http(request =>
            {
                Check.That(request.RequestUri!.AbsolutePath == "/v1/bundles/thumbnails" && request.RequestUri.Query.Contains("bundleIds=126"), "Wrong thumbnail endpoint");
                return Json("""{"data":[{"targetId":126,"state":"Completed","imageUrl":"https://example.test/image.png"}]}""");
            });
            var result = await new RobloxThumbnailClient(http).GetThumbnailAsync(new CatalogItemKey(126, CatalogItemType.Bundle));
            Check.That(result == "https://example.test/image.png", "Bundle image missing");
        });
        suite.AsyncTest("Discovery keeps colliding asset and bundle IDs and requests off-sale collectibles", async () =>
        {
            using var http = Http(request =>
            {
                var url = request.RequestUri!;
                Check.That(url.AbsolutePath == "/v2/search/items/details" && url.Query.Contains("IncludeNotForSale=true") && url.Query.Contains("SalesTypeFilter=2")
                    && url.Query.Contains("Limit=120") && url.Query.Contains("CreatorTargetId=1") && url.Query.Contains("CreatorType=User"), "Incomplete search scope");
                return Json("""{"data":[{"id":126,"itemType":"Asset"},{"id":126,"itemType":"Bundle"},{"id":126,"itemType":2}],"nextPageCursor":"next"}""");
            });
            var page = await new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions())).GetPageAsync(CatalogSearchScope.Collectibles, true, null, default);
            Check.That(page.Items.Count == 2 && page.Items.Select(x => x.Type).Distinct().Count() == 2 && page.NextPageCursor == "next", "Identity collision or cursor loss");
        });
        suite.Test("Migration preserves existing identities and isolates item and retry keys", () =>
        {
            var migration = new AddCatalogItemTypes();
            var columns = migration.UpOperations.OfType<AddColumnOperation>().Where(x => x.Name == "ItemType").ToArray();
            Check.That(columns.Length == 2 && columns.All(x => Equals(x.DefaultValue, "Asset")), "Existing items or retries not classified as assets");
            Check.That(!migration.UpOperations.Any(x => x is DropTableOperation or DeleteDataOperation or DropColumnOperation), "Stored user data removed");
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
            var entity = db.Model.FindEntityType(typeof(Item))!;
            Check.That(entity.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ItemType", "RobloxAssetId" })), "Item uniqueness is not typed");
            var failure = db.Model.FindEntityType(typeof(CatalogImportFailure))!;
            Check.That(failure.FindPrimaryKey()!.Properties.Select(x => x.Name).SequenceEqual(new[] { "ItemType", "AssetId" }), "Retry identity collides");
            var computed = migration.UpOperations.OfType<AlterColumnOperation>().Single(x => x.Name == "MarketplaceUrl");
            Check.That(computed.ComputedColumnSql == entity.FindProperty(nameof(Item.MarketplaceUrl))!.GetComputedColumnSql(), "Migration and model URLs differ");
            Check.That(!db.Database.HasPendingModelChanges(), "Migration snapshot is stale");
            Check.That(migration.DownOperations.OfType<SqlOperation>().Any(x => x.Sql.Contains("RAISE EXCEPTION")), "Unsafe downgrade could erase bundle identity");
        });
    }
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new StubHttp(response)) { BaseAddress = new Uri("https://catalog.roblox.com/") };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };

    private sealed class TypedThumbnail : IRobloxThumbnailClient
    {
        public CatalogItemKey Last;
        public Task<string?> GetAssetThumbnailAsync(long id, CancellationToken cancellationToken = default) => throw new Exception("Untyped thumbnail called");
        public Task<string?> GetThumbnailAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
        {
            Last = key;
            return Task.FromResult<string?>(null);
        }
    }
    private sealed class TypedFailures : ICatalogImportFailureStore
    {
        public HashSet<CatalogItemKey> Keys = [];
        public Task ResolveAsync(CatalogItemKey key, CancellationToken cancellationToken)
        {
            Keys.Remove(key);
            return Task.CompletedTask;
        }
        public Task<bool> CanAttemptAsync(CatalogItemKey key, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetDueItemsAsync(DateTime now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordFailureAsync(CatalogItemKey key, string error, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class TypedRepository : IItemRepository
    {
        public bool OfficialComponent;
        public Task<bool> IsOfficialBundleComponentAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(OfficialComponent && key.Type == CatalogItemType.Asset);
        public Dictionary<CatalogItemKey, Item> Items = [];
        public Task<Item?> GetByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(key));
        public Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.Values.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsByCatalogIdAsync(CatalogItemKey key, CancellationToken cancellationToken = default) => Task.FromResult(Items.ContainsKey(key));
        public Task AddAsync(Item item, CancellationToken cancellationToken = default)
        {
            Items.Add(new CatalogItemKey(item.RobloxAssetId, item.ItemType), item);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Item item, CancellationToken cancellationToken = default)
        {
            Items[new CatalogItemKey(item.RobloxAssetId, item.ItemType)] = item;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CatalogItemKey key, CancellationToken cancellationToken = default)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeferRefreshAsync(CatalogItemKey key, DateTime retry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItemKey>> GetStaleItemsAsync(DateTime due, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

