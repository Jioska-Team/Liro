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
        builder.Services.AddSingleton(new CatalogRefreshPolicy(builder.Configuration.GetValue<TimeSpan?>("Catalog:RefreshInterval"), builder.Configuration.GetValue<TimeSpan?>("Catalog:ThumbnailRefreshInterval")));
        builder.Services.AddSingleton<RobloxRequestGate>();
        builder.Services.AddSingleton<IRobloxRequestBudget, RedisRobloxRequestBudget>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RobloxRequestOptions>>().Value;
            return new RobloxSearchBudget(new RedisRobloxRequestBudget(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                Microsoft.Extensions.Options.Options.Create(new RobloxRequestOptions
                {
                    BudgetKey = options.BudgetKey + ":search",
                    BudgetCapacity = options.SearchBudgetCapacity,
                    BudgetWindow = TimeSpan.FromMinutes(1),
                    MinimumInterval = TimeSpan.Zero
                })));
        });
        builder.Services.AddSingleton<RobloxCatalogSession>();
        builder.Services.AddTransient<RobloxThrottleHandler>();
        builder.Services.AddOptions<RobloxRequestOptions>().BindConfiguration("Roblox:Requests").Validate(x => x.MinimumInterval > TimeSpan.Zero && x.DefaultCooldown > TimeSpan.Zero && x.MaximumFallbackCooldown >= x.DefaultCooldown
            && x.BudgetCapacity is >= 1 and <= 18 && x.BudgetWindow >= TimeSpan.FromMinutes(1) && x.BudgetWindow <= TimeSpan.FromHours(1)
            && x.SearchBudgetCapacity is >= 1 and <= 12
            && !string.IsNullOrWhiteSpace(x.BudgetKey), "Invalid Roblox request pacing options.").ValidateOnStart();
        builder.Services.AddOptions<RobloxCatalogOptions>().BindConfiguration("Roblox:Catalog").Validate(x => x.PageSize is 10 or 28 or 30 or 60 or 120 && x.CreatorTargetId == 1 && x.CreatorType == "User" && (x.Taxonomy == null || x.Taxonomy.Length <= 100) && x.SearchScopes.Length > 0 && x.SearchScopes.All(scope => Enum.IsDefined(scope)), "Invalid Roblox catalog options.").Validate(x => x.CoverageAssetTypeIds.Length <= 100 && x.CoverageAssetTypeIds.All(id => id > 0) && x.CoverageBundleTypeIds.Length <= 20 && x.CoverageBundleTypeIds.All(id => id > 0) && x.CoverageKeywords.Length <= 20 && x.CoverageKeywords.All(word => !string.IsNullOrWhiteSpace(word) && word.Length <= 80) && x.CoverageMaxPartitions is >= 200 and <= 4096 && x.CoverageMaxDepth is >= 0 and <= 20 && x.CoverageResultLimit >= 120, "Invalid catalog coverage configuration.").ValidateOnStart();
        builder.Services.AddScoped<Liro.Application.Catalog.Services.ICatalogCheckpointStore, CatalogCheckpointStore>();
        builder.Services.AddScoped<Liro.Application.Catalog.Services.ICatalogImportFailureStore, CatalogImportFailureStore>();
        builder.Services.AddScoped<IItemMarketHistoryRepository, ItemMarketHistoryRepository>();
        builder.Services.AddScoped<IItemMarketSummaryRepository, ItemMarketSummaryRepository>();
        builder.Services.AddHostedService<DatabaseInitializationService>();
        builder.AddNpgsqlDbContext<LiroDbContext>("lirodb");
        builder.AddRedisClient("valkey");
        builder.Services.AddScoped<IItemRepository, ItemRepository>();
        builder.Services.AddScoped<ICatalogBrowseRepository, CatalogBrowseRepository>();
        builder.Services.AddScoped<ICatalogPageWriter, CatalogPageWriter>();
        builder.Services.AddScoped<ICatalogPriorityQueue, CatalogPriorityQueue>();
        builder.Services.AddScoped<ICatalogLimitedWatchlist, CatalogLimitedWatchlist>();
        builder.Services.AddHttpClient<IRobloxCatalogClient, RobloxCatalogClient>(client =>
        {
            client.BaseAddress = new Uri("https://catalog.roblox.com/");
        }).WithRobloxTrafficControl();
        builder.Services.AddHttpClient<IRobloxComponentClient, RobloxComponentClient>(client =>
        {
            client.BaseAddress = new Uri("https://economy.roblox.com/");
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





