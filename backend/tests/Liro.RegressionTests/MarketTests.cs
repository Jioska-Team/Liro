using System.Net;
using System.Text.Json;
using Liro.Api.Contracts;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Roblox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class MarketTests
{
    private static readonly DateTime ObservationTime = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Catalog retries CSRF challenge once with unchanged payload", async () =>
        {
            var calls = 0;
            string? originalBody = null;
            using var http = new HttpClient(new StubHttp(request =>
            {
                calls++;
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Check.That(!request.Headers.Contains("Cookie"), "Public lookup should not require an account cookie");
                if (calls == 1)
                {
                    originalBody = body;
                    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    challenge.Headers.Add("x-csrf-token", "test-challenge");
                    return challenge;
                }
                Check.That(calls == 2 && body == originalBody, "Retry changed payload or exceeded limit");
                Check.That(request.Headers.GetValues("x-csrf-token").Single() == "test-challenge", "Server token was not sent");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":1,"name":"Item","itemType":"Asset","itemRestrictions":[],"creatorTargetId":1,"assetType":8}]}""") };
            }))
            {
                BaseAddress = new Uri("https://catalog.roblox.com/")
            };
            var item = await new RobloxCatalogClient(http).GetItemAsync(1);
            Check.That(calls == 2 && item?.Details?.CreatorId == 1 && item.Details.AssetTypeId == 8, "Challenge retry did not recover metadata");
        });
        foreach (var withChallenge in new[] { false, true })
        {
            suite.AsyncTest($"Catalog bounds forbidden retries challenge={withChallenge}", async () =>
            {
                var calls = 0;
                using var http = new HttpClient(new StubHttp(_ =>
                {
                    calls++;
                    var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    if (withChallenge)
                        response.Headers.Add("x-csrf-token", "still-forbidden");
                    return response;
                }))
                {
                    BaseAddress = new Uri("https://catalog.roblox.com/")
                };
                var error = await Check.ThrowsAsync<HttpRequestException>(() => new RobloxCatalogClient(http).GetItemAsync(1));
                Check.That(error.StatusCode == HttpStatusCode.Forbidden && calls == (withChallenge ? 2 : 1), "Forbidden response was hidden or retried indefinitely");
            });
        }

        var cases = new (string Name, ItemMarketData Data, string Availability, long? Price)[]
        {
            ("Primary sale", new() { IsOffSale = false, PrimaryPriceRobux = 84 }, "OnSale", 84),
            ("Free primary sale", new() { IsOffSale = false, PrimaryPriceRobux = 0 }, "OnSale", 0),
            ("Offsale with resellers", new() { IsOffSale = true, PrimaryPriceRobux = 0, HasResellers = true, LowestResalePriceRobux = 2400 }, "OnSale", 2400),
            ("Primary cheaper than resale", new() { IsOffSale = false, PrimaryPriceRobux = 84, HasResellers = true, LowestResalePriceRobux = 105 }, "OnSale", 84),
            ("Resale cheaper than primary", new() { IsOffSale = false, PrimaryPriceRobux = 150, HasResellers = true, LowestResalePriceRobux = 105 }, "OnSale", 105),
            ("Confirmed no sellers", new() { IsOffSale = true, HasResellers = false, IsResellable = true }, "NoSellers", null),
            ("Nonresellable offsale", new() { IsOffSale = true, IsResellable = false }, "OffSale", null),
            ("Missing availability", new() { PrimaryPriceRobux = 0 }, "Unknown", null),
            ("Unknown reseller availability", new() { IsOffSale = true, IsResellable = true }, "Unknown", null),
            ("Reseller price missing", new() { IsOffSale = true, HasResellers = true }, "Unknown", null),
            ("Unknown response", new(), "Unknown", null)
        };
        foreach (var scenario in cases)
        {
            suite.Test("Market availability: " + scenario.Name, () =>
            {
                Check.That(scenario.Data.GetAvailability() == scenario.Availability, "Availability mismatch");
                Check.That(scenario.Data.GetAvailablePrice() == scenario.Price, "Available price mismatch");
            });
        }

        suite.Test("Market rejects negative prices and counts", () =>
        {
            foreach (var invalid in new ItemMarketData[]
            {
                new() { PrimaryPriceRobux = -1 }, new() { PriceBeforeDiscountRobux = -1 },
                new() { LowestPriceRobux = -1 }, new() { LowestResalePriceRobux = -1 },
                new() { TotalQuantity = -1 }, new() { UnitsAvailableForConsumption = -1 }, new() { FavoriteCount = -1 }
            })
            {
                Check.Throws<ArgumentOutOfRangeException>(invalid.Validate);
            }
        });
        suite.Test("Market observations retain history and reject older updates", () =>
        {
            var item = Fixtures.Item();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84,
                IsOffSale = false
            }, ObservationTime, "test");
            var first = item.PendingMarketSnapshots.Single();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 105,
                IsOffSale = false
            }, ObservationTime.AddMinutes(1), "test");
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 1
            }, ObservationTime, "test");
            Check.That(item.MarketState!.Data.PrimaryPriceRobux == 105 && first.Data.PrimaryPriceRobux == 84, "Snapshot or current price was overwritten");
            Check.That(item.PendingMarketSnapshots.Count == 2 && first.ItemId == item.Id, "Unexpected snapshot count or item");
        });
        suite.Test("Market partial observation preserves unknown values in history", () =>
        {
            var item = Fixtures.Item();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84,
                FavoriteCount = 10,
                IsOffSale = false
            }, ObservationTime, "test");
            item.ObserveMarket(new()
            {
                FavoriteCount = 0
            }, ObservationTime.AddMinutes(1), "test");
            Check.That(item.MarketState!.Data.PrimaryPriceRobux is null && item.MarketState.Data.FavoriteCount == 0, "Absent and zero were conflated");
            Check.That(item.PendingMarketSnapshots.First().Data.PrimaryPriceRobux == 84, "Previous observation lost");
        });
        suite.Test("Market rejects non UTC timestamps without adding history", () =>
        {
            var item = Fixtures.Item();
            Check.Throws<ArgumentException>(() => item.ObserveMarket(new(), DateTime.SpecifyKind(ObservationTime, DateTimeKind.Unspecified), "test"));
            Check.That(item.PendingMarketSnapshots.Count == 0 && item.MarketState is null, "Invalid observation mutated item");
        });
        suite.Test("Catalog metadata preserves restrictions and remote creation date", () =>
        {
            var item = Fixtures.Item();
            item.UpdateDetails(new()
            {
                CreatorId = 1,
                CreatorType = "User",
                CreatorName = "Roblox",
                AssetTypeId = 46,
                Description = "Wings",
                Restrictions = ["Limited", "Collectible", "Limited"],
                CreatedAtUtc = ObservationTime
            });
            var response = ItemResponse.From(item);
            Check.That(response.CreatorId == 1 && response.AssetTypeId == 46 && response.Description == "Wings", "Metadata lost in API");
            Check.That(response.Restrictions.SequenceEqual(new[] { "Collectible", "Limited" }) && response.RobloxCreatedAtUtc == ObservationTime && response.RobloxUpdatedAtUtc is null, "Restrictions or timestamps fabricated");
        });
        suite.Test("Limited unique transition emits one event across limited classifications", () =>
        {
            var item = Fixtures.Item();
            item.UpdateCatalogData("Item", null, ItemMarketStatus.LimitedUnique);
            item.UpdateCatalogData("Item", null, ItemMarketStatus.Limited);
            Check.That(item.DomainEvents.Count == 1, "Limited family transition duplicated or lost event");
        });

        foreach (var restriction in new[] { "Limited", "LimitedUnique", "Collectible" })
        {
            suite.AsyncTest("Catalog detail mapping: " + restriction, async () =>
            {
                var payload = JsonSerializer.Serialize(new
                {
                    data = new[] { new
                {
                    id = 1, itemType = "Asset", name = "Item", itemRestrictions = new[] { restriction },
                    collectibleItemId = "001e5ae0-5b20-481e-b22c-0633d72b8fbb", creatorType = "User", creatorTargetId = 1,
                    creatorName = "Roblox", assetType = 8, itemCreatedUtc = "2024-01-17T19:37:46.953Z",
                    price = 84, lowestPrice = 84, lowestResalePrice = 105, discountInformation = new { originalPrice = 105 },
                    favoriteCount = 75454, totalQuantity = 10000000, unitsAvailableForConsumption = 9491761,
                    isOffSale = false, hasResellers = true
                } }
                });
                using var http = CreateHttp(payload);
                var remote = (await new RobloxCatalogClient(http).GetItemAsync(1))!;
                var expected = restriction == "LimitedUnique" ? ItemMarketStatus.LimitedUnique : restriction == "Limited" ? ItemMarketStatus.Limited : ItemMarketStatus.Collectible;
                Check.That(remote.MarketStatus == expected && remote.Details!.Restrictions.Single() == restriction, "Restriction collapsed");
                Check.That(remote.Market!.PrimaryPriceRobux == 84 && remote.Market.LowestResalePriceRobux == 105 && remote.Market.LowestPriceRobux == 84 && remote.Market.PriceBeforeDiscountRobux == 105, "Price meanings conflated");
                Check.That(remote.Market.FavoriteCount == 75454 && remote.Market.TotalQuantity == 10000000 && remote.Market.UnitsAvailableForConsumption == 9491761, "Counts lost");
                Check.That(remote.Details!.CreatedAtUtc == DateTime.Parse("2024-01-17T19:37:46.953Z").ToUniversalTime() && remote.Details.UpdatedAtUtc is null, "Remote dates wrong");
            });
        }
        suite.AsyncTest("Catalog accepts numeric enums and keeps missing market fields null", async () =>
        {
            using var http = CreateHttp("""{"data":[{"id":1,"itemType":1,"name":"Item","creatorType":2,"itemRestrictions":[2,9]}]}""");
            var remote = (await new RobloxCatalogClient(http).GetItemAsync(1))!;
            Check.That(remote.MarketStatus == ItemMarketStatus.LimitedUnique && remote.Details!.CreatorType == "Group", "Numeric enum mapping wrong");
            Check.That(remote.Details!.Restrictions.SequenceEqual(new[] { "LimitedUnique", "Collectible" }), "Restrictions lost");
            Check.That(remote.Market!.FavoriteCount is null && remote.Market.PrimaryPriceRobux is null && remote.Market.HasResellers is null, "Missing field became zero or false");
        });
        foreach (var invalid in new[] { "{}", """{"data":[{"id":2,"itemType":"Asset","name":"Other","itemRestrictions":[]}]}""", """{"data":[{"id":1,"itemType":"Bundle","name":"Other","itemRestrictions":[]}]}""" })
        {
            var label = invalid == "{}" ? "missing envelope" : invalid.Contains("Bundle") ? "wrong item type" : "wrong asset ID";
            suite.AsyncTest("Catalog rejects " + label, async () =>
            {
                using var http = CreateHttp(invalid);
                await Check.ThrowsAsync<JsonException>(() => new RobloxCatalogClient(http).GetItemAsync(1));
            });
        }
        suite.AsyncTest("Import persists metadata and market observation together", async () =>
        {
            var repository = new MemoryRepository(Fixtures.Item());
            using var http = CreateHttp("""{"data":[{"id":1,"itemType":"Asset","name":"Wings","itemRestrictions":["LimitedUnique"],"creatorType":"User","creatorTargetId":1,"creatorName":"Roblox","assetType":46,"price":0,"lowestResalePrice":2400,"isOffSale":true,"hasResellers":true,"totalQuantity":463134,"favoriteCount":11104}]}""");
            var importer = new ImportRobloxItemService(new RobloxCatalogClient(http), repository, new PendingThumbnail(), NullLogger<ImportRobloxItemService>.Instance, new MemoryFailureStore(), new CatalogRefreshPolicy());
            var item = (await importer.ImportAsync(1))!;
            Check.That(repository.Saves == 1 && item.Name == "Wings" && item.CreatorId == 1 && item.AssetTypeId == 46, "Metadata not saved with item");
            Check.That(item.MarketStatus == ItemMarketStatus.LimitedUnique && item.Restrictions.Single() == "LimitedUnique", "Classification lost");
            Check.That(item.PendingMarketSnapshots.Single().Data.LowestResalePriceRobux == 2400 && item.MarketState!.Data.GetAvailablePrice() == 2400, "State and snapshot differ");
            Check.That(item.MarketState!.Source == "RobloxCatalogDetailsV1" && item.MarketState.ObservedAtUtc.Kind == DateTimeKind.Utc, "Observation provenance missing");
        });
        suite.AsyncTest("Catalog empty result leaves existing market untouched", async () =>
        {
            var item = Fixtures.Item();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84,
                IsOffSale = false
            }, ObservationTime, "test");
            item.MarkMarketPersisted();
            var repository = new MemoryRepository(item);
            using var http = CreateHttp("""{"data":[]}""");
            var importer = new ImportRobloxItemService(new RobloxCatalogClient(http), repository, new PendingThumbnail(), NullLogger<ImportRobloxItemService>.Instance, new MemoryFailureStore(), new CatalogRefreshPolicy());
            await importer.ImportAsync(1);
            Check.That(item.MarketState!.Data.PrimaryPriceRobux == 84 && item.PendingMarketSnapshots.Count == 0, "Missing item erased market state");
        });
        suite.AsyncTest("Catalog failure does not erase market or append a snapshot", async () =>
        {
            var item = Fixtures.Item();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84,
                IsOffSale = false
            }, ObservationTime, "test");
            item.MarkMarketPersisted();
            var importer = new ImportRobloxItemService(new RateLimitedCatalog(), new MemoryRepository(item), new PendingThumbnail(), NullLogger<ImportRobloxItemService>.Instance, new MemoryFailureStore(), new CatalogRefreshPolicy());
            await Check.ThrowsAsync<HttpRequestException>(() => importer.ImportAsync(1));
            Check.That(item.MarketState!.Data.PrimaryPriceRobux == 84 && item.PendingMarketSnapshots.Count == 0, "Failure changed market");
        });
        suite.AsyncTest("Market state and snapshot remain pending together after failed save", async () =>
        {
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").AddInterceptors(new FailSaveInterceptor()).Options);
            var item = Fixtures.Item();
            item.ObserveMarket(new()
            {
                PrimaryPriceRobux = 84
            }, ObservationTime, "test");
            db.Items.Add(item);
            await Check.ThrowsAsync<IntentionalSaveFailure>(() => db.SaveChangesAsync());
            await Check.ThrowsAsync<IntentionalSaveFailure>(() => db.SaveChangesAsync());
            Check.That(db.Set<ItemMarketSnapshot>().Local.Count == 1 && db.Set<ItemMarketState>().Local.Count == 1, "Save retry duplicated history");
            Check.That(item.PendingMarketSnapshots.Count == 1 && item.MarketState!.IsNew, "Failed save cleared pending market");
        });
    }

    private static HttpClient CreateHttp(string payload)
    {
        var client = new HttpClient(new StubHttp(request =>
        {
            Check.That(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/catalog/items/details", "Wrong catalog endpoint");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            Check.That(document.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64() == 1, "Wrong requested asset");
            Check.That(document.RootElement.GetProperty("items")[0].GetProperty("itemType").GetInt32() == 1, "Wrong requested item type");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            };
        }))
        {
            BaseAddress = new("https://catalog.roblox.com/")
        };
        return client;
    }
}
