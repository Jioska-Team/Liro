using System.Net;
using System.Reflection;
using System.Text.Json;
using Liro.Api;
using Liro.Api.Controllers;
using Liro.Api.Development;
using Liro.Api.Security;
using Liro.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Liro.Application.Catalog.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Liro.RegressionTests;

internal static class HttpOperationalContractTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("HttpContracts_InfrastructureFailureRetainsDiagnostic503Body", async () =>
        {
            await using var host = await Host.Start("Development");
            using var response = await host.Client.GetAsync("/api/infrastructure");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Check.That(response.StatusCode == HttpStatusCode.ServiceUnavailable
                && response.Content.Headers.ContentType?.MediaType == "application/json"
                && root.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "postgres", "valkey", "valkeyLatencyMs" }.Order())
                && root.GetProperty("postgres").GetString() == "error"
                && root.GetProperty("valkey").GetString() == "error"
                && root.GetProperty("valkeyLatencyMs").ValueKind == JsonValueKind.Null,
                "Infrastructure failure changed status, diagnostic fields, null latency or media type");
            Check.That(host.Redis.Calls == 1, "Diagnostic should attempt Valkey exactly once");
            using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/openapi/v1.json"));
            var responses = doc.RootElement.GetProperty("paths").GetProperty("/api/infrastructure").GetProperty("get").GetProperty("responses");
            foreach (var status in new[] { "200", "503" })
                Check.That(responses.GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
                    == "#/components/schemas/InfrastructureResponse", "Diagnostic success and failure must share their diagnostic schema");
        });
        suite.AsyncTest("HttpContracts_InfrastructureIs404InProductionWithoutDependencyCalls", async () =>
        {
            await using var host = await Host.Start("Production");
            using var response = await host.Client.GetAsync("/api/infrastructure");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check.That(response.StatusCode == HttpStatusCode.NotFound
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                && json.RootElement.GetProperty("status").GetInt32() == 404 && host.Redis.Calls == 0,
                "Production diagnostic exposed details, changed its problem response, or contacted dependencies");
        });
        suite.AsyncTest("HttpContracts_LanReadinessDocumentsResponsesAndKeepsLocal404Empty", async () =>
        {
            await using var host = await Host.Start("Development");
            using var response = await host.Client.GetAsync("/api/lan/ready");
            Check.That(response.StatusCode == HttpStatusCode.NotFound
                && (await response.Content.ReadAsByteArrayAsync()).Length == 0 && host.Redis.Calls == 0,
                "LAN probe on the local listener must remain an empty 404 without dependency calls");
            using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/openapi/v1.json"));
            var responses = doc.RootElement.GetProperty("paths").GetProperty("/api/lan/ready").GetProperty("get").GetProperty("responses");
            foreach (var status in new[] { "200", "503" })
                Check.That(responses.GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
                    == "#/components/schemas/LanReadinessResponse", "LAN readiness status lacks its existing response schema");
            Check.That(!responses.GetProperty("404").TryGetProperty("content", out var content) || !content.EnumerateObject().Any(),
                "LAN 404 documentation must not invent a body");
            var limited = responses.GetProperty("429");
            Check.That(limited.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()
                == "#/components/schemas/ProblemDetails" && limited.GetProperty("headers").TryGetProperty("Retry-After", out _),
                "LAN throttle contract lacks its problem body or Retry-After header");
        });
    }

    private sealed class Host(WebApplication app, HttpClient client, ThrowingRedisProxy redis) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public ThrowingRedisProxy Redis => redis;
        public static async Task<Host> Start(string environment)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            builder.Services.RemoveAll<ItemService>();
            builder.Services.RemoveAll<ImportRobloxItemService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(InfrastructureController).Assembly);
            // No configured provider: CanConnectAsync fails locally and never opens a database connection.
            builder.Services.AddSingleton(new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().Options));
            var cache = DispatchProxy.Create<IConnectionMultiplexer, ThrowingRedisProxy>();
            var proxy = (ThrowingRedisProxy)(object)cache;
            builder.Services.AddSingleton(cache);
            var app = builder.Build();
            app.UseExceptionHandler();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapControllers();
            if (environment == "Development")
                app.MapLanReadiness(LanReadinessTests.Access());
            app.MapOpenApi();
            await app.StartAsync();
            return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) }, proxy);
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    public class ThrowingRedisProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Dispose") return null;
            if (targetMethod?.Name == "DisposeAsync") return ValueTask.CompletedTask;
            if (targetMethod?.Name == "GetDatabase")
            {
                Calls++;
                throw new InvalidOperationException("Test-only cache is unavailable.");
            }
            throw new NotSupportedException("Unexpected cache operation: " + targetMethod?.Name);
        }
    }
}
