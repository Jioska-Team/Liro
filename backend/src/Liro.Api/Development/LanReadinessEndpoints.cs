using Liro.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Liro.Api.Development;

public sealed record LanReadinessResponse(string Service, string Status);

public static class LanReadinessEndpoints
{
    public static void MapLanReadiness(this WebApplication app, DevelopmentLanAccess access)
    {
        if (access.Enabled)
        {
            app.MapGet("/api/lan/ready", async (HttpContext context, LiroDbContext database, IConnectionMultiplexer cache) =>
            {
                var result = await CheckAsync(context, access, token => database.Database.CanConnectAsync(token),
                    async token => { await cache.GetDatabase().PingAsync().WaitAsync(token); });
                return result;
            })
            .WithDescription("Development LAN dependency probe. Available only on the configured LAN listener.")
            .Produces<LanReadinessResponse>(StatusCodes.Status200OK)
            .Produces<LanReadinessResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        }
    }

    public static async Task<IResult> CheckAsync(HttpContext context, DevelopmentLanAccess access, Func<CancellationToken, Task<bool>> database, Func<CancellationToken, Task> cache)
    {
        if (!access.IsLanRequest(context))
        {
            return Results.NotFound();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var ready = false;
        try
        {
            ready = await database(deadline.Token).WaitAsync(deadline.Token);
            await cache(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ready = false;
        }

        var result = Results.Json(new LanReadinessResponse("liro-api", ready ? "ready" : "unavailable"), statusCode: ready ? 200 : 503);
        return result;
    }
}
