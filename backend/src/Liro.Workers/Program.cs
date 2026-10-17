using Liro.Application.Catalog.Services;
using Liro.Infrastructure;
using Liro.Workers.Catalog;
using Liro.Workers.Outbox;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddScoped<ImportRobloxItemService>();
builder.Services.AddScoped<CatalogImportProcessor>();
builder.Services.AddOptions<CatalogWorkerOptions>().BindConfiguration("CatalogWorkers").Validate(x => x.LimitedPollInterval >= TimeSpan.FromSeconds(15) && x.RefreshIdleDelay > TimeSpan.Zero && x.RetryDelay > TimeSpan.Zero && x.CycleInterval > TimeSpan.Zero && x.ItemDelay >= TimeSpan.Zero && x.PageDelay >= TimeSpan.Zero && x.DetailsBatchSize is > 0 and <= 100 && x.BatchSize is > 0 and <= 500 && x.DiscoveryPages is > 0 and <= 100, "Invalid catalog worker intervals or batch sizes.").Validate(x => x.KnownLimitedBatchSize is > 0 and <= 100 && x.KnownLimitedInterval >= TimeSpan.FromSeconds(15) && x.ComplementaryLimitedInterval >= TimeSpan.FromMinutes(1) && x.RecentItemLimit is > 0 and <= 10000 && x.RecentLimitedLimit is > 0 and <= 10000, "Invalid limited monitoring limits.").ValidateOnStart();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<CatalogBootstrapWorker>();
builder.Services.AddHostedService<CatalogRefreshWorker>();
builder.Services.AddHostedService<CatalogLimitedMonitor>();
builder.Services.AddHostedService<CatalogKnownLimitedWorker>();
builder.Services.AddHostedService<CatalogComplementaryLimitedWorker>();
builder.Services.AddHostedService<CatalogDiscoveryWorker>();
builder.Services.AddHostedService<BundleComponentsWorker>();
await builder.Build().RunAsync();


