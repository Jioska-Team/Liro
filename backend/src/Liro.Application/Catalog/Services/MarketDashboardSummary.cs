namespace Liro.Application.Catalog.Services;

/// <summary>Stored statistics for unique items currently registered as Limited or LimitedUnique.</summary>
public sealed record MarketDashboardSummary(
    DateTime? UpdatedAt, bool IsStale, MarketCapStatistic MarketCap, MarketVolumeStatistic Volume,
    LimitedCountStatistic Limiteds, MarketPulseStatistic MarketPulse)
{
    public string Universe => "RegisteredLimiteds";
    public static MarketDashboardSummary Empty => new(null, true, new(), new(), new(null, null), MarketPulseStatistic.Insufficient(0, 0));
}

public sealed record MarketCapStatistic
{
    public decimal? Value => null;
    public decimal? Change24h => null;
    public decimal? CoveragePercent => null;
    public bool IsEstimated => true;
    public string Status => "Unavailable";
    public string Unit => "Robux";
    // TotalQuantity/remaining initial stock do not verify circulating supply.
    // Public inventory pages can redact owners, including on their final page;
    // neither counting all copies nor subtracting redacted copies proves circulation.
    public string Reason => "UnverifiedCirculatingSupply";
}

public sealed record MarketVolumeStatistic
{
    public decimal? Value { get; init; }
    public decimal? ChangePercent { get; init; }
    public decimal? CoveragePercent { get; init; }
    public bool IsEstimated => true;
    public string Status { get; init; } = "Unavailable";
    public string Unit => "Robux";
    public DateTime? PeriodStartUtc { get; init; }
    public DateTime? PeriodEndUtc { get; init; }
    public string PeriodKind => "PreviousUtcDay";
    public int IncludedItems { get; init; }
    public int ComparisonItems { get; init; }
}
public sealed record LimitedCountStatistic(int? Total, int? WeeklyChange);
public sealed record MarketPulseStatistic(
    string Status, decimal? BullishPercentage, decimal? BearishPercentage, decimal? NeutralPercentage,
    int UpItems, int DownItems, int UnchangedItems, int AnalyzedItems, decimal? CoveragePercent,
    decimal? DirectionalBullishPercentage)
{
    public static MarketPulseStatistic Insufficient(int analyzed, int total) =>
        new("InsufficientData", null, null, null, 0, 0, 0, analyzed, MarketDashboardCalculator.Coverage(analyzed, total), null);
}

public sealed class MarketDashboardOptions
{
    public int IntervalMinutes { get; set; } = 15;
    public int MaxSnapshotAgeMinutes { get; set; } = 60;
    public int MaxRapAgeMinutes { get; set; } = 180;
    public int BaselineToleranceMinutes { get; set; } = 180;
    public int MinimumPulseItems { get; set; } = 30;
    public decimal MinimumPulseCoveragePercent { get; set; } = 60;
    public decimal BullishThresholdPercent { get; set; } = 60;
    public decimal BearishThresholdPercent { get; set; } = 40;
    public bool IsValid() => IntervalMinutes is >= 5 and <= 60 && MaxSnapshotAgeMinutes >= IntervalMinutes
        && MaxSnapshotAgeMinutes <= 1440 && MaxRapAgeMinutes is >= 1 and <= 360
        && BaselineToleranceMinutes is >= 1 and <= 360 && MinimumPulseItems >= 1
        && MinimumPulseCoveragePercent is > 0 and <= 100
        && BearishThresholdPercent is >= 0 and < 50 && BullishThresholdPercent is > 50 and <= 100;
}

public interface IMarketDashboardRepository
{
    Task<MarketDashboardSummary> GetLatestAsync(DateTime now, CancellationToken cancellationToken);
    Task<bool> CaptureAsync(DateTime now, CancellationToken cancellationToken);
}

public static class MarketDashboardCalculator
{
    public static decimal? Change(decimal current, decimal previous) =>
        previous > 0 ? (current - previous) / previous * 100m : null;
    public static decimal? Coverage(int included, int total) => total > 0 ? included * 100m / total : null;
    public static MarketPulseStatistic Pulse(int up, int down, int unchanged, int total, MarketDashboardOptions options)
    {
        var valid = up + down + unchanged;
        var coverage = Coverage(valid, total);
        if (valid < options.MinimumPulseItems || coverage < options.MinimumPulseCoveragePercent || coverage is null)
            return MarketPulseStatistic.Insufficient(valid, total) with { UpItems = up, DownItems = down, UnchangedItems = unchanged };
        // Classification uses the whole valid cohort, including unchanged observations;
        // public breadth percentages still use ALL valid observations and sum to 100.
        var directional = up + down == 0 ? (decimal?)null : up * 100m / (up + down);
        var status = up * 100m / valid >= options.BullishThresholdPercent
            ? "Bullish" : down * 100m / valid >= 100m - options.BearishThresholdPercent ? "Bearish" : "Neutral";
        return new(status, up * 100m / valid, down * 100m / valid, unchanged * 100m / valid,
            up, down, unchanged, valid, coverage, directional);
    }
}
