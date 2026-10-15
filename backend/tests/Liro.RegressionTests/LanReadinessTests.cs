using Liro.Api.Development;
using Liro.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Liro.RegressionTests;

internal static class LanReadinessTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var ready in new[] { true, false })
        {
            suite.AsyncTest("LAN readiness reports dependency state " + ready, async () =>
            {
                var context = new DefaultHttpContext();
                context.Connection.LocalPort = 5205;
                var result = await LanReadinessEndpoints.CheckAsync(context, Access(), _ => Task.FromResult(ready), _ => Task.CompletedTask);
                Check.That(((IStatusCodeHttpResult)result).StatusCode == (ready ? 200 : 503), "Incorrect readiness status");
                var value = (LanReadinessResponse)((IValueHttpResult)result).Value!;
                Check.That(value.Service == "liro-api" && value.Status == (ready ? "ready" : "unavailable"), "Incorrect readiness identity");
            });
        }
        suite.AsyncTest("LAN readiness hides dependency exceptions", async () =>
        {
            var context = new DefaultHttpContext();
            context.Connection.LocalPort = 5205;
            var result = await LanReadinessEndpoints.CheckAsync(context, Access(), _ => Task.FromResult(true), _ => Task.FromException(new Exception("sensitive connection")));
            Check.That(((IStatusCodeHttpResult)result).StatusCode == 503, "Dependency error escaped");
            Check.That(!System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value).Contains("sensitive"), "Leaked dependency details");
        });
        suite.AsyncTest("LAN readiness is unavailable on local management endpoint", async () =>
        {
            var called = false;
            var result = await LanReadinessEndpoints.CheckAsync(new DefaultHttpContext(), Access(), _ => { called = true; return Task.FromResult(true); }, _ => Task.CompletedTask);
            Check.That(((IStatusCodeHttpResult)result).StatusCode == 404 && !called, "Management endpoint exposed LAN probe");
        });
    }

    private static DevelopmentLanAccess Access()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lan:Enabled"] = "true",
            ["Lan:Address"] = "192.168.50.10",
            ["Lan:PrefixLength"] = "24",
            ["Lan:AllowedOrigins:0"] = "http://localhost:4200"
        }).Build();
        var access = DevelopmentLanAccess.Create(configuration, "Development");
        return access;
    }
}
