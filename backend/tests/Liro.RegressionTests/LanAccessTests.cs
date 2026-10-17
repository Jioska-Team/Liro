using System.Net;
using Liro.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Liro.RegressionTests;

internal static class LanAccessTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("La red local permite leer paquetes y componentes pero bloquea escritura y rutas parecidas", async () =>
        {
            foreach (var path in new[] { "/api/v1/catalog", "/api/v1/catalog/bundles/1/components", "/api/v1/catalog/assets/1/bundles" })
            {
                var called = false;
                await Access().InvokeAsync(Context("192.168.50.20", path), _ => { called = true; return Task.CompletedTask; });
                Check.That(called, "Lectura bloqueada");
                var write = Context("192.168.50.20", path);
                write.Request.Method = "POST";
                await VerifyDenied(write);
            }
            await VerifyDenied(Context("192.168.50.20", "/api/v1/catalog-other"));
        });
        suite.Test("LAN access is disabled by default", () =>
        {
            var access = DevelopmentLanAccess.Create(new ConfigurationBuilder().Build(), "Production");
            Check.That(!access.Enabled, "LAN enabled without opt-in");
        });

        foreach (var path in new[] { "/api/infrastructure", "/openapi/v1.json", "/health", "/api/v1/items-other" })
        {
            suite.AsyncTest("LAN blocks non-catalog route " + path, async () =>
            {
                var context = Context("192.168.50.20", path);
                await VerifyDenied(context);
            });
        }

        suite.AsyncTest("LAN rejects writes even with an admin header", async () =>
        {
            var context = Context("192.168.50.20", "/api/v1/items/import/1");
            context.Request.Method = "POST";
            context.Request.Headers["X-Liro-Admin-Key"] = new string('a', 40);
            await VerifyDenied(context);
        });

        foreach (var peer in new[] { "192.168.51.20", "8.8.8.8", "127.0.0.1" })
        {
            suite.AsyncTest("LAN rejects foreign peer " + peer, async () =>
            {
                var context = Context(peer, "/api/v1/items/by-asset/1");
                context.Request.Headers["X-Forwarded-For"] = "192.168.50.20";
                await VerifyDenied(context);
            });
        }

        foreach (var peer in new[] { "192.168.50.20", "::ffff:192.168.50.21" })
        {
            suite.AsyncTest("LAN permits subnet catalog reads " + peer, async () =>
            {
                var called = false;
                await Access().InvokeAsync(Context(peer, "/api/v1/items/by-asset/1"), _ =>
                {
                    called = true;
                    return Task.CompletedTask;
                });
                Check.That(called, "Allowed catalog request was blocked");
            });
        }

        suite.AsyncTest("LAN leaves localhost HTTPS administration unchanged", async () =>
        {
            var context = Context("127.0.0.1", "/api/infrastructure");
            context.Connection.LocalPort = 7174;
            var called = false;
            await Access().InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });
            Check.That(called, "Local endpoint was restricted");
        });

        foreach (var invalid in new[]
        {
            ("Lan:Address", "0.0.0.0"), ("Lan:Address", "8.8.8.8"),
            ("Lan:PrefixLength", "0"), ("Lan:PrefixLength", "31"),
            ("Lan:AllowedOrigins:0", "*"), ("Lan:AllowedOrigins:0", "https://example.com/path")
        })
        {
            suite.Test("LAN rejects invalid configuration " + invalid, () =>
            {
                var rejected = false;
                try
                {
                    Access(invalid.Item1, invalid.Item2);
                }
                catch (InvalidOperationException) { rejected = true; }
                Check.That(rejected, "Unsafe LAN settings accepted");
            });
        }
        suite.Test("LAN cannot be enabled in production", () =>
        {
            var rejected = false;
            try
            {
                Access(environment: "Production");
            }
            catch (InvalidOperationException) { rejected = true; }
            Check.That(rejected, "Production accepted development exposure");
        });

        foreach (var origin in new[] { "http://localhost:4200", "https://untrusted.example" })
        {
            suite.AsyncTest("LAN CORS preflight restricts origins " + origin, async () =>
            {
                var access = Access();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddCors(access.ConfigureCors);
                using var provider = services.BuildServiceProvider();
                var pipeline = new ApplicationBuilder(provider);
                pipeline.Use(access.InvokeAsync);
                pipeline.UseCors(DevelopmentLanAccess.CorsPolicyName);
                pipeline.Run(_ => throw new Exception("Preflight should not reach catalog services"));
                var context = Context("192.168.50.20", "/api/v1/items/by-asset/1");
                context.RequestServices = provider;
                context.Request.Method = "OPTIONS";
                context.Request.Headers.Origin = origin;
                context.Request.Headers.AccessControlRequestMethod = "GET";
                await pipeline.Build()(context);
                Check.That(context.Response.StatusCode == 204, "Preflight did not complete");
                var allowed = context.Response.Headers.AccessControlAllowOrigin.ToString();
                Check.That(allowed == (origin == "http://localhost:4200" ? origin : ""), "CORS allowed an unexpected origin");
                Check.That(!context.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"), "LAN should not allow credentialed browser requests");
                if (allowed.Length > 0)
                {
                    Check.That(!context.Response.Headers.AccessControlAllowMethods.ToString().Contains("POST"), "CORS permits writes");
                    Check.That(!context.Response.Headers.AccessControlAllowHeaders.ToString().Contains("X-Liro-Admin-Key"), "CORS permits administrative headers");
                }
            });
        }
    }

    private static DevelopmentLanAccess Access(string? key = null, string? value = null, string environment = "Development")
    {
        var values = new Dictionary<string, string?>
        {
            ["Lan:Enabled"] = "true",
            ["Lan:Address"] = "192.168.50.10",
            ["Lan:PrefixLength"] = "24",
            ["Lan:AllowedOrigins:0"] = "http://localhost:4200"
        };
        if (key is not null)
        {
            values[key] = value;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var access = DevelopmentLanAccess.Create(configuration, environment);
        return access;
    }

    private static DefaultHttpContext Context(string peer, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Connection.LocalPort = 5205;
        context.Request.Method = "GET";
        context.Request.Path = path;
        return context;
    }

    private static async Task VerifyDenied(DefaultHttpContext context)
    {
        var called = false;
        await Access().InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });
        Check.That(!called && context.Response.StatusCode == 403, "LAN boundary allowed forbidden access");
    }
}
