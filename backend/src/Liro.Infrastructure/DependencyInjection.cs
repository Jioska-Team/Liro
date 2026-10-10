using Liro.Application.Catalog.Services;
using Microsoft.Extensions.Configuration;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Common.Events;
using Liro.Application.RobloxIntegration;
using Liro.Domain.Catalog.Events;
using Liro.Infrastructure.Events;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Liro.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new CatalogRefreshPolicy(builder.Configuration.GetValue<TimeSpan?>("Catalog:RefreshInterval")));
        builder.Services.AddSingleton<RobloxRequestGate>();
        builder.Services.AddTransient<RobloxThrottleHandler>();
        builder.Services.AddOptions<RobloxRequestOptions>().BindConfiguration("Roblox:Requests").Validate(x => x.MinimumInterval > TimeSpan.Zero && x.DefaultCooldown > TimeSpan.Zero && x.MaximumFallbackCooldown >= x.DefaultCooldown, "Invalid Roblox request pacing options.").ValidateOnStart();
        builder.Services.AddOptions<RobloxCatalogOptions>().BindConfiguration("Roblox:Catalog").Validate(x => x.PageSize is 10 or 28 or 30 && x.Category >= 0 && (x.CreatorTargetId is null or > 0) && x.CreatorType is "User" or "Group", "Invalid Roblox catalog options.").ValidateOnStart();
        builder.Services.AddScoped<Liro.Application.Catalog.Services.ICatalogCheckpointStore, CatalogCheckpointStore>();
        builder.Services.AddScoped<Liro.Application.Catalog.Services.ICatalogImportFailureStore, CatalogImportFailureStore>();
        builder.Services.AddScoped<IItemMarketHistoryRepository, ItemMarketHistoryRepository>();
        builder.Services.AddHostedService<DatabaseInitializationService>();
        builder.AddNpgsqlDbContext<LiroDbContext>("lirodb");
        builder.AddRedisClient("valkey");
        builder.Services.AddScoped<IItemRepository, ItemRepository>();
        builder.Services.AddHttpClient<IRobloxCatalogClient, RobloxCatalogClient>(client =>
        {
            client.BaseAddress = new Uri("https://catalog.roblox.com/");
        }).WithRobloxTrafficControl();
        builder.Services.AddHttpClient<IRobloxThumbnailClient, RobloxThumbnailClient>(client =>
        {
            client.BaseAddress = new Uri("https://thumbnails.roblox.com/");
        }).WithRobloxTrafficControl();
        builder.Services.AddHttpClient<IRobloxCatalogDiscoveryClient, RobloxCatalogDiscoveryClient>(client =>
        {
            client.BaseAddress = new Uri("https://catalog.roblox.com/");
        }).WithRobloxTrafficControl();
        builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        builder.Services.AddScoped<IDomainEventHandler<ItemBecameLimited>, ItemBecameLimitedLogHandler>();
        return builder;
    }
}
