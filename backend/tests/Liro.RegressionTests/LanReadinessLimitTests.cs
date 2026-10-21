using System.Net;
using System.Threading.RateLimiting;
using Liro.Api;
using Liro.Api.Development;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class LanReadinessLimitTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("LanReadiness_IndependentFrequencyAndRouteVariants", async () =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLiroApi(new ConfigurationBuilder().Build());
            using var provider = services.BuildServiceProvider();
            using var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
            DefaultHttpContext Request(string path, int client, string method = "GET")
            {
                var context = new DefaultHttpContext();
                context.Request.Path = path;
                context.Request.Method = method;
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.50." + client);
                context.Request.Headers["X-Forwarded-For"] = Guid.NewGuid().ToString();
                return context;
            }
            // Exhaust a catalog client's budget first; readiness must retain its own allowance.
            for (var i = 0; i < 60; i++)
            {
                using var lease = await limiter.AcquireAsync(Request("/api/v1/catalog", 2));
                Check.That(lease.IsAcquired, "Catalog budget changed");
            }
            for (var i = 0; i < 60; i++)
            {
                using var lease = await limiter.AcquireAsync(Request(i % 2 == 0 ? "/api/lan/ready" : "/API/LAN/READY/", i % 2 + 2, i % 2 == 0 ? "GET" : "HEAD"));
                Check.That(lease.IsAcquired, "Readiness rejected within its independent budget");
            }
            using var denied = await limiter.AcquireAsync(Request("/api/lan/ready", 4));
            Check.That(!denied.IsAcquired && denied.TryGetMetadata(MetadataName.RetryAfter, out var delay) && delay > TimeSpan.Zero,
                "Readiness frequency can be bypassed by another client, method or path variant");
            using var catalog = await limiter.AcquireAsync(Request("/api/v1/catalog", 5));
            using var health = await limiter.AcquireAsync(Request("/health", 2));
            Check.That(catalog.IsAcquired && health.IsAcquired, "Readiness exhausted unrelated budgets");
        });

        suite.AsyncTest("LanReadiness_ConcurrencyRejectsBeforeDependenciesAndRecovers", async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            await using var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            var entered = 0;
            var cacheCalls = 0;
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var access = LanReadinessTests.Access();
            app.MapGet("/api/lan/ready", async (HttpContext context) =>
            {
                // The test listener is ephemeral; CheckAsync still uses the configured LAN port.
                context.Connection.LocalPort = 5205;
                return await LanReadinessEndpoints.CheckAsync(context, access, async token =>
                {
                    if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return true;
                }, _ => { Interlocked.Increment(ref cacheCalls); return Task.CompletedTask; });
            });
            app.MapGet("/api/v1/catalog", () => Results.Ok());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            var first = client.GetAsync("/api/lan/ready");
            var second = client.GetAsync("/api/lan/ready");
            try
            {
                await both.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using var rejected = await client.GetAsync("/api/lan/ready");
                Check.That(rejected.StatusCode == HttpStatusCode.TooManyRequests && rejected.Headers.RetryAfter is not null,
                    "Third concurrent readiness request was not rejected with Retry-After");
                Check.That(entered == 2 && cacheCalls == 0, "Rejected request reached dependencies");
                using var catalog = await client.GetAsync("/api/v1/catalog");
                Check.That(catalog.IsSuccessStatusCode, "Readiness concurrency blocked catalog");
                release.TrySetResult();
                using var one = await first;
                using var two = await second;
                using var recovered = await client.GetAsync("/api/lan/ready");
                Check.That(one.IsSuccessStatusCode && two.IsSuccessStatusCode && recovered.IsSuccessStatusCode
                    && entered == 3 && cacheCalls == 3, "Completed probes did not release capacity");
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(first, second);
                await app.StopAsync();
            }
        });

        suite.AsyncTest("LanReadiness_DependencyFailureReleasesCapacity", async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLiroApi(builder.Configuration);
            await using var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            var entered = 0;
            app.MapGet("/api/lan/ready", async (HttpContext context) =>
            {
                context.Connection.LocalPort = 5205;
                return await LanReadinessEndpoints.CheckAsync(context, LanReadinessTests.Access(), _ =>
                {
                    Interlocked.Increment(ref entered);
                    return Task.FromException<bool>(new Exception("private dependency details"));
                }, _ => Task.CompletedTask);
            });
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var response = await client.GetAsync("/api/lan/ready");
                    Check.That(response.StatusCode == HttpStatusCode.ServiceUnavailable
                        && !(await response.Content.ReadAsStringAsync()).Contains("private dependency details"),
                        "Failed dependency leaked details or retained concurrency capacity");
                }
                Check.That(entered == 3, "Failed probes retained capacity");
            }
            finally { await app.StopAsync(); }
        });
    }
}
