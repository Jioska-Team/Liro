using System.Text.Json;
using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;

namespace Liro.RegressionTests;

internal static class ItemPriceTests
{
 public static void Register(RegressionSuite suite)
 {
  suite.Test("ItemPrices_DiscountAndMissingPricesHaveDistinctMeaning", () =>
  {
   var item = new Item(1,"Discounted");
   item.ObserveMarket(new ItemMarketData { PriceBeforeDiscountRobux=50000, PrimaryPriceRobux=40000, IsOffSale=false }, DateTime.UtcNow,"test");
   var response = ItemResponse.From(item);
   Check.That(response.OriginalPriceRobux==50000 && response.CurrentPriceRobux==40000 && response.RapRobux is null,"Discount prices were lost or RAP invented");
   var missing = ItemResponse.From(new Item(2,"Unknown"));
   using var json = JsonDocument.Parse(JsonSerializer.Serialize(missing,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
   foreach (var name in new[]{"originalPriceRobux","currentPriceRobux","rapRobux"})
    Check.That(json.RootElement.GetProperty(name).ValueKind==JsonValueKind.Null,"Missing price must serialize as explicit null: "+name);
  });
  suite.Test("ItemPrices_PublicListingUsesBasePriceWithoutInventingPlusDiscount", () =>
  {
   foreach(var (data, original, current) in new (ItemMarketData,long?,long?)[] {
    (new(){PrimaryPriceRobux=50000,IsOffSale=false},50000,50000),
    (new(){PrimaryPriceRobux=0,IsOffSale=false},0,0),
    (new(){PrimaryPriceRobux=1,IsOffSale=true},null,null),
    (new(){PrimaryPriceRobux=50000},null,null) })
   {
    var item=new Item(117367851700030,"Emberwrath");
    item.ObserveMarket(data,DateTime.UtcNow,"test");
    var response=ItemResponse.From(item);
    Check.That(response.OriginalPriceRobux==original && response.CurrentPriceRobux==current && response.RapRobux is null,"Public base price or missing availability was misrepresented");
   }
  });
  suite.Test("ItemPrices_FreeOffSaleAndResalePreserveAvailability", () =>
  {
   foreach(var (data,expected) in new (ItemMarketData,long?)[] {
    (new(){PrimaryPriceRobux=0,IsOffSale=false},0),
    (new(){PrimaryPriceRobux=100,IsOffSale=true},null),
    (new(){PrimaryPriceRobux=100},null),
    (new(){PrimaryPriceRobux=100,IsOffSale=true,LowestResalePriceRobux=120,HasResellers=true},120),
    (new(){PrimaryPriceRobux=100,IsOffSale=false,LowestResalePriceRobux=80,HasResellers=true},80),
    (new(){LowestResalePriceRobux=80,HasResellers=false},null) })
   {
    var item = new Item(1,"Availability",marketStatus:ItemMarketStatus.Limited);
    item.ObserveMarket(data,DateTime.UtcNow,"test");
    var response=ItemResponse.From(item,25.5m);
    Check.That(response.CurrentPriceRobux==expected && response.OriginalPriceRobux is null && response.RapRobux==25.5m,"Availability, unknown original, or decimal RAP changed");
    Check.That(response.Market!.AvailablePriceRobux==expected,"Existing price contract differs");
   }
  });
  suite.Test("ItemPrices_RapOnlyForLimitedStatuses", () =>
  {
   foreach(var status in Enum.GetValues<ItemMarketStatus>())
   {
    var response=ItemResponse.From(new Item(1,"Status",marketStatus:status),0);
    Check.That(response.RapRobux==(status is ItemMarketStatus.Limited or ItemMarketStatus.LimitedUnique ? 0m : null),"RAP exposed for wrong status or zero became null");
   }
  });
  suite.AsyncTest("ItemPrices_BatchRapPreservesOrderAndSkipsNonLimited",async () =>
  {
   var normal=new Item(1,"Normal"); var limited=new Item(2,"Limited",marketStatus:ItemMarketStatus.Limited);
   var unique=new Item(3,"Unique",marketStatus:ItemMarketStatus.LimitedUnique);
   var store=new RapStore(); store.Values[limited.Id]=12.5m;
   var mapper=new ItemResponseMapper(store);
   var rows=await mapper.MapAsync(new[]{normal,limited,unique,limited},default);
   Check.That(store.Calls==1 && store.Requested.Count==2 && !store.Requested.Contains(normal.Id),"RAP reads must be one batch of distinct Limited IDs");
   Check.That(rows.Select(x=>x.Id).SequenceEqual(new[]{normal.Id,limited.Id,unique.Id,limited.Id}) && rows[0].RapRobux is null && rows[1].RapRobux==12.5m && rows[2].RapRobux is null && rows[3].RapRobux==12.5m,"Batch mapping lost identity, missing RAP or order");
   await mapper.MapAsync(new[]{normal},default); await mapper.MapAsync(Array.Empty<Item>(),default);
   Check.That(store.Calls==1,"Empty/non-Limited response queried RAP");
  });
 }
 internal sealed class RapStore : IItemMarketSummaryRepository
 {
  public Dictionary<Guid,decimal?> Values { get; }=new();
  public int Calls; public IReadOnlyCollection<Guid> Requested=Array.Empty<Guid>();
  public Task<ItemMarketSummary?> GetAsync(Guid itemId,CancellationToken ct)=>Task.FromResult<ItemMarketSummary?>(null);
  public Task<IReadOnlyDictionary<Guid,decimal?>> GetRapAsync(IReadOnlyCollection<Guid> itemIds,CancellationToken ct)
  { Calls++; Requested=itemIds; return Task.FromResult<IReadOnlyDictionary<Guid,decimal?>>(Values); }
 }
}
