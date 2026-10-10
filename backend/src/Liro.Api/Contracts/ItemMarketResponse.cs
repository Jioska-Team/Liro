using Liro.Domain.Catalog.Models;

namespace Liro.Api.Contracts;

public sealed record ItemMarketResponse(ItemMarketData Data, DateTime ObservedAtUtc, string Source, string Availability, long? AvailablePriceRobux)
{
    public static ItemMarketResponse From(ItemMarketData data, DateTime observedAtUtc, string source)
    {
        var response = new ItemMarketResponse(data, observedAtUtc, source, data.GetAvailability(), data.GetAvailablePrice());
        return response;
    }
}
