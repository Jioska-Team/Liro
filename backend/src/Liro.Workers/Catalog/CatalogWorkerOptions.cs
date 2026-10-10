namespace Liro.Workers.Catalog;

public sealed class CatalogWorkerOptions
{
    public bool BootstrapEnabled { get; set; } = true;
    public bool RefreshEnabled { get; set; } = true;
    public bool DiscoveryEnabled { get; set; } = true;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ItemDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan PageDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; set; } = 50;
    public int DiscoveryPages { get; set; } = 5;
}
