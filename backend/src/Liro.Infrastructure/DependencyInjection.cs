using Liro.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace Liro.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        builder.AddNpgsqlDbContext<LiroDbContext>("lirodb");
        builder.AddRedisClient("valkey");

        return builder;
    }
}