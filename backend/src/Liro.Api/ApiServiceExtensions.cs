using System.Threading.RateLimiting;
using Liro.Api.Security;
using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api;

public static class ApiServiceExtensions
{
    public static IServiceCollection AddLiroApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers();
        services.AddOpenApi(ApiOpenApi.Configure);
        services.AddScoped<ItemService>();
        services.AddScoped<Liro.Api.Contracts.ItemResponseMapper>();
        services.AddScoped<ImportRobloxItemService>();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddAuthentication(AdminAuthenticationOptions.SchemeName).AddScheme<AdminAuthenticationOptions, AdminAuthenticationHandler>(AdminAuthenticationOptions.SchemeName, options => options.ApiKey = configuration["Security:AdminApiKey"]);
        services.AddAuthorization(options => options.AddPolicy("CatalogAdmin", policy => policy.AddAuthenticationSchemes(AdminAuthenticationOptions.SchemeName).RequireAuthenticatedUser()));
        services.AddRateLimiter(options =>
        {
            CatalogReadLimits.Configure(options);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Shared per API process, only for the new history routes; no queue competing with workers.
            options.AddConcurrencyLimiter("market-history-reads", limiter =>
            {
                limiter.PermitLimit = 2;
                limiter.QueueLimit = 0;
            });
            options.AddFixedWindowLimiter("catalog-writes", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
        });
        return services;
    }
}
