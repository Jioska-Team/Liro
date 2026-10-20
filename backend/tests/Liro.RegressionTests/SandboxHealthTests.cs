using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Liro.RegressionTests;

internal static class SandboxHealthTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var entry in new[] { ("Development", false, true), ("Staging", false, false),
            ("Staging", true, true), ("Production", true, false) })
        {
            suite.AsyncTest($"HealthEndpoints_{entry.Item1}_OptIn_{entry.Item2}", async () =>
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = entry.Item1 });
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["HealthChecks:ExposeEndpoints"] = entry.Item2.ToString() });
                builder.Services.AddHealthChecks();
                await using var app = builder.Build();
                app.MapDefaultEndpoints();
                var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints)
                    .OfType<RouteEndpoint>().Select(x => x.RoutePattern.RawText).ToArray();
                if (routes.Contains("/health") != entry.Item3 || routes.Contains("/alive") != entry.Item3)
                    throw new Exception("Health endpoints exposed in the wrong environment.");
            });
        }
    }
}
