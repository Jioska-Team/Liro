using System.Net;
using System.Text.Json;
using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class RobloxTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Discovery preserves HTTP failure status", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new("https://catalog.roblox.com/")
            };
            try
            {
                await new RobloxCatalogDiscoveryClient(http, Microsoft.Extensions.Options.Options.Create(new RobloxCatalogOptions())).GetCatalogPageAsync();
                throw new Exception("Expected failure");
            }
            catch (HttpRequestException e)
            {
                Check.That(e.StatusCode == HttpStatusCode.InternalServerError, "HTTP status was discarded");
            }
        });
        suite.AsyncTest("Discovery rejects missing data instead of declaring completion", async () =>
        {
            using var http = new HttpClient(new StubHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }))
            {
                BaseAddress = new("https://catalog.roblox.com/")
            };
            try
            {
                await new RobloxCatalogDiscoveryClient(http, Microsoft.Extensions.Options.Options.Create(new RobloxCatalogOptions())).GetCatalogPageAsync();
                throw new Exception("Malformed page accepted");
            }
            catch (JsonException)
            {
            }
        });
        suite.AsyncTest("Pagination uses encoded cursor and consistent configured scope", async () =>
        {
            Uri? requested = null;
            using var http = new HttpClient(new StubHttp(request =>
            {
                requested = request.RequestUri;
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":1,\"itemType\":\"Asset\"},{\"id\":1,\"itemType\":\"Asset\"},{\"id\":2,\"itemType\":\"Bundle\"}],\"nextPageCursor\":null}")
                };
            }))
            {
                BaseAddress = new("https://catalog.roblox.com/")
            };
            var client = new RobloxCatalogDiscoveryClient(http, Microsoft.Extensions.Options.Options.Create(new RobloxCatalogOptions { CreatorTargetId = null }));
            var result = await client.GetRecentlyUpdatedPageAsync("abc&injected=true");
            Check.That(
                requested!.AbsolutePath == "/v1/search/items/details" && requested.Query.Contains("SortType=3") && requested.Query.Contains("abc%26injected%3Dtrue") && !requested.Query.Contains("CreatorTargetId"),
                "Pagination escaped the configured contract");
            Check.That(result.AssetIds.Count == 1 && result.AssetIds[0] == 1, "Nonassets or duplicates leaked into discovery");
        });
        suite.AsyncTest("429 pauses all Roblox clients without automatic retry bursts", async () =>
        {
            var calls = 0;
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Testing" });
            builder.Services.AddLogging();
            builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
            }));
            Liro.Infrastructure.DependencyInjection.AddInfrastructure(builder);
            builder.Services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = new StubHttp(_ =>
            {
                Interlocked.Increment(ref calls);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            })));
            using var provider = builder.Services.BuildServiceProvider();
            await Check.ThrowsAsync<RobloxRateLimitedException>(() => provider.GetRequiredService<IRobloxCatalogClient>().GetItemAsync(1));
            await Check.ThrowsAsync<RobloxRateLimitedException>(() => provider.GetRequiredService<IRobloxCatalogDiscoveryClient>().GetCatalogPageAsync());
            Check.That(calls == 1, $"Sent {calls} HTTP requests despite provider cooldown");
        });
        suite.AsyncTest("Rate limiting is not recorded as a defective asset", async () =>
        {
            var failures = new MemoryFailureStore();
            var repo = new MemoryRepository(Fixtures.Item());
            var importer = new ImportRobloxItemService(
                new RateLimitedCatalog(),
                repo,
                new PendingThumbnail(),
                NullLogger<ImportRobloxItemService>.Instance,
                failures, new CatalogRefreshPolicy());
            var processor = new Liro.Workers.Catalog.CatalogImportProcessor(importer, repo, failures, NullLogger<Liro.Workers.Catalog.CatalogImportProcessor>.Instance);
            var exception = await Check.ThrowsAsync<HttpRequestException>(() => processor.ProcessAsync(1, false, CancellationToken.None));
            Check.That(exception.StatusCode == HttpStatusCode.TooManyRequests, "The 429 status was not propagated");
            Check.That(
                await failures.CanAttemptAsync(1, DateTime.UtcNow, CancellationToken.None),
                "A 429 consumed the asset failure budget");
        });
        foreach (var useDate in new[]
        {
            false,
            true
        }

        )
        {
            suite.AsyncTest("Provider cooldown honors Retry-After " + (useDate ? "date" : "seconds") + " and resumes", async () =>
            {
                var clock = new ManualClock();
                var now = clock.Now;
                var sends = 0;
                using var gate = new RobloxRequestGate(
                    Microsoft.Extensions.Options.Options.Create(new RobloxRequestOptions()),
                    clock,
                    NullLogger<RobloxRequestGate>.Instance);
                var exception = await Check.ThrowsAsync<RobloxRateLimitedException>(() => gate.SendAsync(_ =>
                {
                    sends++;
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = useDate ? new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(2)) : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                    return Task.FromResult(response);
                }, CancellationToken.None));
                Check.That(exception.RetryAtUtc == now.AddMinutes(2), "Provider retry deadline ignored");
                clock.Now = now.AddMinutes(1);
                try
                {
                    await gate.SendAsync(_ =>
                    {
                        sends++;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                    }, CancellationToken.None);
                    throw new Exception("Request admitted during cooldown");
                }
                catch (RobloxRateLimitedException)
                {
                }

                clock.Now = now.AddMinutes(2);
                using var ok = await gate.SendAsync(_ =>
                {
                    sends++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }, CancellationToken.None);
                Check.That(sends == 2 && ok.IsSuccessStatusCode, "Cooldown did not block then resume");
            });
        }

        suite.AsyncTest("Missing Retry-After uses increasing shared cooldown", async () =>
        {
            var clock = new ManualClock();
            using var gate = new RobloxRequestGate(
                Microsoft.Extensions.Options.Options.Create(new RobloxRequestOptions()),
                clock,
                NullLogger<RobloxRequestGate>.Instance);
            foreach (var minutes in new[]
            {
                1,
                2,
                4
            }

            )
            {
                var now = clock.Now;
                try
                {
                    await gate.SendAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)), CancellationToken.None);
                    throw new Exception("Expected rate limit");
                }
                catch (RobloxRateLimitedException e)
                {
                    Check.That(e.RetryAtUtc == now.AddMinutes(minutes), "Fallback cooldown did not increase");
                    clock.Now = e.RetryAtUtc;
                }
            }
        });
        suite.AsyncTest("Successful requests are paced across callers", async () =>
        {
            using var gate = new RobloxRequestGate(
                Microsoft.Extensions.Options.Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.FromMilliseconds(40) }),
                TimeProvider.System,
                NullLogger<RobloxRequestGate>.Instance);
            using var first = await gate.SendAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), CancellationToken.None);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var second = await gate.SendAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), CancellationToken.None);
            Check.That(watch.ElapsedMilliseconds >= 25, "No shared pacing between successful calls");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await gate.SendAsync(_ => throw new Exception("Cancelled request was sent"), cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
