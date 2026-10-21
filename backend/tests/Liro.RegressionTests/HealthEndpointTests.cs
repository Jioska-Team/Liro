using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Liro.RegressionTests;

internal static class HealthEndpointTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var entry in new[] { ("Development", true), ("Staging", false), ("Production", false) })
        {
            suite.AsyncTest($"HealthEndpoints_{entry.Item1}", async () =>
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = entry.Item1 });
                builder.Services.AddHealthChecks();
                await using var app = builder.Build();
                app.MapDefaultEndpoints();
                var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints)
                    .OfType<RouteEndpoint>().Select(x => x.RoutePattern.RawText).ToArray();
                if (routes.Contains("/health") != entry.Item2 || routes.Contains("/alive") != entry.Item2)
                    throw new Exception("Health endpoints exposed in the wrong environment.");
            });
        }
    }
}
