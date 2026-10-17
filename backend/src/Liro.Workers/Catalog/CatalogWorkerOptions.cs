namespace Liro.Workers.Catalog;

public sealed class CatalogWorkerOptions
{
    public bool KnownLimitedMonitorEnabled { get; set; } = true;
    public int KnownLimitedBatchSize { get; set; } = 100;
    public TimeSpan KnownLimitedInterval { get; set; } = TimeSpan.FromMinutes(1);
    public bool ComplementaryLimitedSearchEnabled { get; set; } = true;
    public TimeSpan ComplementaryLimitedInterval { get; set; } = TimeSpan.FromMinutes(15);
    public int RecentItemLimit { get; set; } = 1200;
    public int RecentLimitedLimit { get; set; } = 600;
    public bool BoundedRecentDiscovery { get; set; }
    public bool BoundedLimitedDiscovery { get; set; } = true;
    public bool HistoricalRefreshEnabled { get; set; } = true;
    public bool LimitedMonitorEnabled { get; set; } = true;
    public TimeSpan LimitedPollInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RefreshIdleDelay { get; set; } = TimeSpan.FromSeconds(15);
    public bool BootstrapEnabled { get; set; } = true;
    public bool RefreshEnabled { get; set; } = true;
    public bool DiscoveryEnabled { get; set; } = true;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ItemDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan PageDelay { get; set; } = TimeSpan.Zero;
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromMinutes(5);
    public int DetailsBatchSize { get; set; } = 10;
    public int BatchSize { get; set; } = 50;
    public int DiscoveryPages { get; set; } = 5;
}

