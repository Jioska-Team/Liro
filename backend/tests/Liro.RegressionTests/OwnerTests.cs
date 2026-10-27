using System.Net;
using System.Text.Json;
using Liro.Api.Controllers;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Infrastructure.Roblox;
using Microsoft.AspNetCore.Mvc;

namespace Liro.RegressionTests;

internal static class OwnerTests
{
    private const string Instance = "c6555337-7ef2-4cb8-8c57-3a878afe8f55";
    private const long Asset = 7584199117;
    private static string Payload(string owner = "null") => "{\"data\":[{\"id\":131081988300,\"collectibleItemInstanceId\":\"" + Instance + "\",\"serialNumber\":1,\"owner\":" + owner + ",\"created\":\"2021-10-01T05:00:32.103Z\",\"updated\":\"2021-10-01T05:00:32.103Z\"}],\"nextPageCursor\":\"next/token+=\",\"previousPageCursor\":null}";
    private static ItemOwnerPage Parse(string json) { using var doc = JsonDocument.Parse(json); return RobloxOwnerClient.Parse(doc.RootElement, new ManualClock().Now.UtcDateTime); }
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Owner pages preserve real copy identity and redacted owners without deletion assumptions", () =>
        {
            var page = Parse(Payload()); var copy = page.Copies.Single();
            Check.That(copy.Uaid == 131081988300 && copy.InstanceId == Instance && copy.SerialNumber == 1, "Copy identity was lost");
            Check.That(copy.OwnerId is null && copy.OwnerName is null && copy.OwnerType is null, "Redacted owner was fabricated");
            Check.That(page.Status == "Available" && page.Source == "Roblox" && page.NextCursor == "next/token+=" && page.PreviousCursor is null, "Page contract mismatch");
            var json = JsonSerializer.Serialize(page);
            Check.That(!json.Contains("Deleted") && !json.Contains("Premium") && !json.Contains("Created") && !json.Contains("Updated") && !json.Contains("Total"), "Unsupported ownership metadata was invented");
        });
        foreach (var type in new[] { "1", "2", "\"User\"", "\"Group\"" })
            suite.Test("Owner pages retain public owner identities with documented type " + type, () =>
            {
                var copy = Parse(Payload("{\"id\":12,\"name\":\"Public owner\",\"type\":" + type + "}")).Copies.Single();
                Check.That(copy.OwnerId == 12 && copy.OwnerName == "Public owner" && copy.OwnerType == (type is "1" or "\"User\"" ? "User" : "Group"), "Public owner mapping changed");
            });
        suite.Test("Owner terminal pages do not certify circulating supply from visible or redacted owners", () =>
        {
            // Even a fully exhausted page can mix public owners with private or deleted owners.
            // Exhaustion proves only that the source has no next page, not that ownership is known.
            var page = Parse("""
                {"data":[
                  {"id":1001,"serialNumber":1,"owner":{"id":12,"type":"User","name":"Visible"}},
                  {"id":1002,"serialNumber":2,"owner":{"id":12,"type":"User","name":"Visible"}},
                  {"id":1003,"serialNumber":3,"owner":null}
                ],"nextPageCursor":null,"previousPageCursor":null}
                """);
            Check.That(page.Copies.Count == 3 && page.NextCursor is null
                && page.Copies.Count(copy => copy.OwnerId == 12) == 2 && page.Copies.Single(copy => copy.Uaid == 1003).OwnerId is null,
                "A hidden copy was discarded or copies with the same owner were merged");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Check.That(!json.RootElement.TryGetProperty("circulatingSupply", out _)
                && !json.RootElement.TryGetProperty("deletedCopies", out _)
                && !json.RootElement.TryGetProperty("totalOwners", out _),
                "Page exhaustion or redacted ownership produced unsupported global counts");
        });
        suite.Test("Owner pages keep absent serials and legacy instance identifiers nullable", () =>
        {
            var copy = Parse(Payload().Replace("\"serialNumber\":1", "\"serialNumber\":null").Replace("\"" + Instance + "\"", "null")).Copies.Single();
            Check.That(copy.SerialNumber is null && copy.InstanceId is null && copy.Uaid > 0, "Legacy copy lost UAID or gained invented data");
            Check.That(Parse("{\"data\":[],\"nextPageCursor\":null,\"previousPageCursor\":null}").Status == "Available", "Empty page was confused with an unavailable source");
        });
        foreach (var sample in new (string Name,string Json)[]
        {
            ("missing array", "{}"), ("invalid UAID",Payload().Replace("131081988300","0")),
            ("invalid instance",Payload().Replace(Instance,"invalid")),
            ("negative serial",Payload().Replace("\"serialNumber\":1","\"serialNumber\":-1")),
            ("invalid owner",Payload("{\"id\":0,\"type\":1}")),
            ("long cursor",Payload().Replace("next/token+=",new string('a',1025)))
        }) suite.Test("Owner pages reject invalid provider data: " + sample.Name,()=>Check.Throws<JsonException>(()=>Parse(sample.Json)));
        suite.Test("Owner pages reject duplicate copies and oversized pages", () =>
        {
            using var doc = JsonDocument.Parse(Payload()); var entry=doc.RootElement.GetProperty("data")[0].GetRawText();
            Check.Throws<JsonException>(()=>Parse("{\"data\":["+entry+","+entry+"]}"));
            Check.Throws<JsonException>(()=>Parse("{\"data\":["+string.Join(",",Enumerable.Repeat(entry,11))+"]}"));
        });
        suite.AsyncTest("Owner client encodes cursors and reuses only unexpired matching pages", async () =>
        {
            var clock=new ManualClock();var calls=new List<string>();
            using var http=new HttpClient(new StubHttp(r=>{calls.Add(r.RequestUri!.AbsoluteUri);return new(HttpStatusCode.OK){Content=new StringContent(Payload())};})){BaseAddress=new Uri("https://inventory.roblox.com/")};
            var client=new RobloxOwnerClient(http,new RobloxCatalogSession(clock),new RobloxOwnerCache(clock),clock);
            var first=await client.GetAsync(Asset,null,default);
            Check.That(ReferenceEquals(first,await client.GetAsync(Asset,null,default)) && calls.Count==1,"Cached page was fetched again");
            await client.GetAsync(Asset,"next/token+=&limit=100",default);
            Check.That(calls.Count==2 && calls[1].EndsWith("?sortOrder=Asc&limit=10&cursor=next%2Ftoken%2B%3D%26limit%3D100"),"Cursor changed URL structure");
            clock.Now=clock.Now.AddSeconds(119);await client.GetAsync(Asset,null,default);Check.That(calls.Count==2,"Page expired too early");
            clock.Now=clock.Now.AddSeconds(1);await client.GetAsync(Asset,null,default);Check.That(calls.Count==3,"Expired page was reused");
        });
        suite.AsyncTest("Owner client shares concurrent lookups and does not cache unavailable upstream responses", async () =>
        {
            var clock=new ManualClock();var handler=new BlockingHandler();
            using var http=new HttpClient(handler){BaseAddress=new Uri("https://inventory.roblox.com/")};
            var client=new RobloxOwnerClient(http,new RobloxCatalogSession(clock),new RobloxOwnerCache(clock),clock);
            var first=client.GetAsync(Asset,null,default);var second=client.GetAsync(Asset,null,default);
            Check.That(handler.Calls==1,"Concurrent requests were duplicated");handler.Response.SetResult(new(HttpStatusCode.Forbidden));
            await Check.ThrowsAsync<HttpRequestException>(()=>first);await Check.ThrowsAsync<HttpRequestException>(()=>second);
            handler.Response=new(TaskCreationOptions.RunContinuationsAsynchronously);var retry=client.GetAsync(Asset,null,default);
            handler.Response.SetResult(new(HttpStatusCode.OK){Content=new StringContent(Payload())});await retry;
            Check.That(handler.Calls==2,"Failed response poisoned cache");
        });
        suite.AsyncTest("Owner client honors cancellation without cancelling another shared reader", async () =>
        {
            var clock=new ManualClock();var handler=new BlockingHandler();
            using var http=new HttpClient(handler){BaseAddress=new Uri("https://inventory.roblox.com/")};
            var client=new RobloxOwnerClient(http,new RobloxCatalogSession(clock),new RobloxOwnerCache(clock),clock);
            using var cancelled=new CancellationTokenSource();var first=client.GetAsync(Asset,null,cancelled.Token);var second=client.GetAsync(Asset,null,default);
            cancelled.Cancel();await Check.ThrowsAsync<OperationCanceledException>(()=>first);
            handler.Response.SetResult(new(HttpStatusCode.OK){Content=new StringContent(Payload())});
            Check.That((await second).Copies.Count==1 && handler.Calls==1,"One cancelled reader cancelled the shared request");
        });
        suite.AsyncTest("Owner service resolves assets and distinguishes missing unsupported and valid pages", async () =>
        {
            var remote=new Client();
            foreach(var item in new[]{new Item(1,"normal"),new Item(2,"bundle",marketStatus:ItemMarketStatus.Limited,itemType:CatalogItemType.Bundle)})
            {
                var service=new ItemOwnerService(new MemoryRepository(item),remote);
                Check.That((await service.GetAsync(item.Id,null,10,default))?.Status=="Unavailable","Unsupported item used provider");
                Check.That(await service.GetAsync(Guid.NewGuid(),null,10,default) is null,"Missing item was confused with unsupported");
            }
            Check.That(remote.Calls==0,"Unsupported items used budget");
            var limited=new Item(Asset,"limited",marketStatus:ItemMarketStatus.LimitedUnique);
            var supported=new ItemOwnerService(new MemoryRepository(limited),remote);
            await supported.GetAsync(limited.Id,"cursor",10,default);
            Check.That(remote.Calls==1 && remote.Id==Asset && remote.Cursor=="cursor","Asset was confused with internal GUID or collectible ID");
            await Check.ThrowsAsync<ArgumentException>(()=>supported.GetAsync(limited.Id,null,100,default));
            await Check.ThrowsAsync<ArgumentException>(()=>supported.GetAsync(limited.Id,new string('a',1025),10,default));
            await Check.ThrowsAsync<ArgumentException>(()=>supported.GetAsync(limited.Id,"\n",10,default));
            Check.That(remote.Calls==1,"Invalid query used provider");
            var controller=new ItemOwnersController(supported);
            Check.That(await controller.Get(Guid.NewGuid(),default) is NotFoundResult,"Missing HTTP status changed");
            Check.That(await controller.Get(limited.Id,default) is OkObjectResult{Value:ItemOwnerPage},"HTTP contract changed");
        });
        suite.Test("Owner cache evicts old pages at its retention limit", () =>
        {
            var clock=new ManualClock();var cache=new RobloxOwnerCache(clock);
            for(var i=0;i<501;i++){cache.Store(i.ToString(),new("Available",[],null,null,clock.Now.UtcDateTime));clock.Now=clock.Now.AddMilliseconds(1);}
            Check.That(cache.Find("0") is null && cache.Find("500") is not null,"Cache retained unbounded pages");
        });
    }
    private sealed class Client:IRobloxOwnerClient
    {
        public int Calls;public long Id;public string? Cursor;
        public Task<ItemOwnerPage> GetAsync(long id,string? cursor,CancellationToken ct){Calls++;Id=id;Cursor=cursor;return Task.FromResult(Parse(Payload()));}
    }
    private sealed class BlockingHandler:HttpMessageHandler
    {
        public int Calls;public TaskCompletionSource<HttpResponseMessage> Response=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Response.Task.WaitAsync(ct);}
    }
}
