using System.Net;
using System.Text.Json;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Infrastructure.Roblox;
using Microsoft.AspNetCore.Mvc;

namespace Liro.RegressionTests;

internal static class ResellerTests
{
    private const string Instance = "2385406c-e128-46bd-8788-fe0944fd1941";
    private static readonly Guid Collectible = Guid.Parse("001e5ae0-5b20-481e-b22c-0633d72b8fbb");
    private static string Payload(string fields = "", string seller = "{\"sellerId\":2044335993,\"sellerType\":\"User\",\"name\":\"seller\"}") =>
        "{\"data\":[{\"collectibleItemInstanceId\":\"" + Instance + "\",\"price\":2050,\"serialNumber\":40125,\"seller\":" + seller + fields + "}],\"nextPageCursor\":\"next/token+=\",\"previousPageCursor\":null}";
    private static ItemResellerPage Parse(string json) { using var doc = JsonDocument.Parse(json); return RobloxResellerClient.Parse(doc.RootElement, new ManualClock().Now.UtcDateTime); }
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Reseller offers preserve seller identity serial price and cursors without owner totals", () =>
        {
            var page = Parse(Payload());
            var offer = page.Offers.Single();
            Check.That(page.Status == "Available" && page.Source == "Roblox" && page.NextCursor == "next/token+=" && page.PreviousCursor is null, "Page metadata was lost");
            Check.That(offer.InstanceId == Instance && offer.PriceRobux == 2050 && offer.SerialNumber == 40125 && offer.SellerId == 2044335993 && offer.SellerType == "User" && offer.SellerName == "seller", "Offer identity or price mismatch");
            Check.That(!JsonSerializer.Serialize(page).Contains("Total"), "Partial page was reported as a total");
        });
        suite.Test("Resellers retain zero price and null seller and serial instead of fabricating owners", () =>
        {
            var offer = Parse(Payload(seller: "null").Replace("2050", "0").Replace("40125", "null")).Offers.Single();
            Check.That(offer.PriceRobux == 0 && offer.SellerId is null && offer.SellerName is null && offer.SellerType is null && offer.SerialNumber is null, "Unknown owner was fabricated");
            Check.That(Parse("{\"data\":[],\"nextPageCursor\":null,\"previousPageCursor\":null}").Status == "Available", "Empty page was confused with unavailable data");
        });
        foreach (var sample in new (string Name, string Json)[]
        {
            ("missing array", "{}"),
            ("negative price", Payload().Replace("2050", "-1")),
            ("invalid instance", Payload().Replace(Instance, "invalid")),
            ("negative serial", Payload().Replace("40125", "-1")),
            ("invalid seller ID", Payload().Replace("2044335993", "0")),
            ("fractional Robux", Payload().Replace("2050", "1.5"))
        })
            suite.Test("Resellers reject invalid provider data: " + sample.Name,
                () => Check.Throws<JsonException>(() => Parse(sample.Json)));
        suite.Test("Resellers reject duplicate instances and oversized pages", () =>
        {
            using var doc = JsonDocument.Parse(Payload());
            var entry = doc.RootElement.GetProperty("data")[0].GetRawText();
            Check.Throws<JsonException>(() => Parse("{\"data\":[" + entry + "," + entry + "]}"));
            Check.Throws<JsonException>(() => Parse("{\"data\":[" + string.Join(",", Enumerable.Repeat(entry, 11)) + "]}"));
        });
        suite.AsyncTest("Reseller client encodes cursor caches pages separately and expires at two minutes", async () =>
        {
            var clock = new ManualClock(); var calls = new List<string>();
            using var http = new HttpClient(new StubHttp(request => { calls.Add(request.RequestUri!.AbsoluteUri); return new(HttpStatusCode.OK) {Content = new StringContent(Payload())}; })) {BaseAddress = new Uri("https://apis.roblox.com/")};
            var client = new RobloxResellerClient(http, new RobloxCatalogSession(clock), new RobloxResellerCache(clock), clock);
            var first = await client.GetAsync(Collectible, null, default);
            Check.That(ReferenceEquals(first, await client.GetAsync(Collectible, null, default)) && calls.Count == 1, "Fresh page was fetched again");
            await client.GetAsync(Collectible, "next/token+=&limit=100", default);
            Check.That(calls.Count == 2 && calls[1].EndsWith("?limit=10&cursor=next%2Ftoken%2B%3D%26limit%3D100"), "Cursor escaped its query value or pages shared cache");
            clock.Now = clock.Now.AddSeconds(119);
            await client.GetAsync(Collectible, null, default);
            Check.That(calls.Count == 2, "Cache expired too early");
            clock.Now = clock.Now.AddSeconds(1);
            await client.GetAsync(Collectible, null, default);
            Check.That(calls.Count == 3, "Cache reused an expired page");
        });
        suite.AsyncTest("Reseller client shares concurrent lookups and does not cache provider failures", async () =>
        {
            var clock = new ManualClock(); var handler = new BlockingHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://apis.roblox.com/") };
            var client = new RobloxResellerClient(http, new RobloxCatalogSession(clock), new RobloxResellerCache(clock), clock);
            var first = client.GetAsync(Collectible, null, default); var second = client.GetAsync(Collectible, null, default);
            Check.That(handler.Calls == 1, "Concurrent requests were duplicated");
            handler.Response.SetResult(new(HttpStatusCode.ServiceUnavailable));
            await Check.ThrowsAsync<HttpRequestException>(() => first);
            await Check.ThrowsAsync<HttpRequestException>(() => second);
            handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = client.GetAsync(Collectible, null, default);
            handler.Response.SetResult(new(HttpStatusCode.OK) {Content = new StringContent(Payload())});
            await retry;
            Check.That(handler.Calls == 2, "Provider failure poisoned cache");
        });
        suite.AsyncTest("Reseller service distinguishes missing unsupported and supported items", async () =>
        {
            var remote = new Client();
            foreach (var item in new[] { new Item(1,"normal"), new Item(2,"missing guid", marketStatus:ItemMarketStatus.Limited), new Item(3,"bundle",Collectible,ItemMarketStatus.Limited,CatalogItemType.Bundle) })
            {
                var service = new ItemResellerService(new MemoryRepository(item), remote);
                Check.That((await service.GetAsync(item.Id,null,10,default))?.Status == "Unavailable", "Unsupported item requested a provider");
                Check.That(await service.GetAsync(Guid.NewGuid(),null,10,default) is null, "Missing item was not distinguished");
            }
            Check.That(remote.Calls == 0, "Unsupported queries consumed provider budget");
            var limited = new Item(4,"limited",Collectible,ItemMarketStatus.LimitedUnique);
            var supported = new ItemResellerService(new MemoryRepository(limited),remote);
            await supported.GetAsync(limited.Id,"cursor",10,default);
            Check.That(remote.Calls == 1 && remote.Id == Collectible && remote.Cursor == "cursor", "Internal ID did not resolve to collectible ID");
            await Check.ThrowsAsync<ArgumentException>(() => supported.GetAsync(limited.Id,null,100,default));
            await Check.ThrowsAsync<ArgumentException>(() => supported.GetAsync(limited.Id,new string('a',1025),10,default));
            await Check.ThrowsAsync<ArgumentException>(() => supported.GetAsync(limited.Id,"\n",10,default));
            Check.That(remote.Calls == 1, "Invalid query consumed provider budget");
            var controller = new ItemResellersController(supported);
            Check.That(await controller.Get(Guid.NewGuid(),default) is NotFoundResult, "Controller missing identity status changed");
            Check.That(await controller.Get(limited.Id,default) is OkObjectResult {Value:ItemResellerPage}, "Controller did not return typed page");
        });
        suite.Test("Reseller cache bounds retained pages", () =>
        {
            var clock = new ManualClock(); var cache = new RobloxResellerCache(clock);
            for (var i = 0; i < 501; i++)
            {
                cache.Store(i.ToString(), new("Available",[],null,null,clock.Now.UtcDateTime));
                clock.Now = clock.Now.AddMilliseconds(1);
            }
            Check.That(cache.Find("0") is null && cache.Find("500") is not null, "Old cache entries were not evicted");
        });
    }
    private sealed class Client : IRobloxResellerClient
    {
        public int Calls; public Guid Id; public string? Cursor;
        public Task<ItemResellerPage> GetAsync(Guid id, string? cursor, CancellationToken ct) { Calls++; Id=id; Cursor=cursor; return Task.FromResult(Parse(Payload())); }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Response.Task.WaitAsync(ct); }
    }
}
