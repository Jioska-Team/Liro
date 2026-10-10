namespace Liro.Domain.Catalog.Models;

// A provider observation. Null means unavailable, never zero or false.
public sealed record ItemMarketData
{
    public long? PrimaryPriceRobux
    {
        get; init;
    }
    public long? PriceBeforeDiscountRobux
    {
        get; init;
    }
    public long? LowestPriceRobux
    {
        get; init;
    }
    public long? LowestResalePriceRobux
    {
        get; init;
    }
    public bool? IsOffSale
    {
        get; init;
    }
    public bool? HasResellers
    {
        get; init;
    }
    public bool? IsResellable
    {
        get; init;
    }
    public string? PriceStatus
    {
        get; init;
    }
    public long? TotalQuantity
    {
        get; init;
    }
    public long? UnitsAvailableForConsumption
    {
        get; init;
    }
    public long? FavoriteCount
    {
        get; init;
    }

    public void Validate()
    {
        if (PrimaryPriceRobux < 0 || PriceBeforeDiscountRobux < 0 || LowestPriceRobux < 0
            || LowestResalePriceRobux < 0 || TotalQuantity < 0 || UnitsAvailableForConsumption < 0 || FavoriteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ItemMarketData), "Market prices and counts cannot be negative.");
        }
    }

    public long? GetAvailablePrice()
    {
        var primary = IsOffSale == false ? PrimaryPriceRobux : null;
        var resale = HasResellers == true ? LowestResalePriceRobux : null;
        var price = primary.HasValue && resale.HasValue ? Math.Min(primary.Value, resale.Value) : primary ?? resale;
        return price;
    }

    public string GetAvailability()
    {
        if (GetAvailablePrice().HasValue)
        {
            return "OnSale";
        }

        if (HasResellers == true || IsOffSale == false)
        {
            return "Unknown";
        }

        if (IsOffSale == true && HasResellers == false && IsResellable == true)
        {
            return "NoSellers";
        }

        var availability = IsOffSale == true && (IsResellable != true || HasResellers.HasValue) ? "OffSale" : "Unknown";
        return availability;
    }
}
