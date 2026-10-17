using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Liro.Infrastructure.Roblox;

public static class RobloxHttpClientExtensions
{
    public static IHttpClientBuilder WithRobloxTrafficControl(this IHttpClientBuilder client)
    {
        // Queue/pacing runs outside the network timeout. Keep one resilience pipeline, not two.
#pragma warning disable EXTEXP0001 // Documented opt-out API in the pinned resilience package; keep this suppression local.

        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        client.AddHttpMessageHandler<RobloxThrottleHandler>();
        client.AddStandardResilienceHandler(options =>
        {
            // Retry at the durable worker/client boundary so each attempt re-enters the gate
            // and the global budget. An inner retry would send unaccounted HTTP requests.
            options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            var circuit = options.CircuitBreaker.ShouldHandle;
            options.CircuitBreaker.ShouldHandle = args => args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests ? ValueTask.FromResult(false) : circuit(args);
        });
        return client;
    }
}
