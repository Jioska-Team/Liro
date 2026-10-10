using Liro.Application.Catalog.Services;
using Liro.Infrastructure;
using Liro.Workers.Catalog;
using Liro.Workers.Outbox;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddScoped<ImportRobloxItemService>();
builder.Services.AddScoped<CatalogImportProcessor>();
builder.Services.AddOptions<CatalogWorkerOptions>().BindConfiguration("CatalogWorkers").Validate(x => x.RetryDelay > TimeSpan.Zero && x.CycleInterval > TimeSpan.Zero && x.ItemDelay >= TimeSpan.Zero && x.PageDelay >= TimeSpan.Zero && x.BatchSize is > 0 and <= 500 && x.DiscoveryPages is > 0 and <= 100, "Invalid catalog worker intervals or batch sizes.").ValidateOnStart();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<CatalogBootstrapWorker>();
builder.Services.AddHostedService<CatalogRefreshWorker>();
builder.Services.AddHostedService<CatalogDiscoveryWorker>();
await builder.Build().RunAsync();
