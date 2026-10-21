using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Security;

public static class CatalogReadLimits
{
    // API admission only. Never shares or changes the outbound Roblox request budget.
    public static void Configure(RateLimiterOptions options)
    {
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsRead(context)
                ? RateLimitPartition.GetConcurrencyLimiter("catalog", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 2,
                    QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("unrestricted")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsRead(context)
                ? RateLimitPartition.GetFixedWindowLimiter("catalog", _ => Window(120))
                : RateLimitPartition.GetNoLimiter("unrestricted")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsRead(context)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => Window(60))
                : RateLimitPartition.GetNoLimiter("unrestricted")),
            // Independent admission for the LAN dependency probe: no queued database work.
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsLanReadiness(context)
                ? RateLimitPartition.GetConcurrencyLimiter("lan-readiness", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 2,
                    QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("unrestricted")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsLanReadiness(context)
                ? RateLimitPartition.GetFixedWindowLimiter("lan-readiness", _ => Window(60))
                : RateLimitPartition.GetNoLimiter("unrestricted")));
        options.OnRejected = async (context, ct) =>
        {
            var delay = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromSeconds(1);
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "Demasiadas solicitudes. Reintenta después del intervalo indicado.").ExecuteAsync(context.HttpContext);
        };
    }

    private static FixedWindowRateLimiterOptions Window(int limit) => new()
    {
        PermitLimit = limit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    };

    private static bool IsLanReadiness(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        (context.Request.Path.Value ?? "").TrimEnd('/').Equals("/api/lan/ready", StringComparison.OrdinalIgnoreCase);
    private static bool IsRead(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        (context.Request.Path.StartsWithSegments("/api/v1/catalog") || context.Request.Path.StartsWithSegments("/api/v1/items"));
}
