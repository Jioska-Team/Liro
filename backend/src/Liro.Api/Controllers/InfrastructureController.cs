using Liro.Api.Contracts;
using Liro.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ApiController]
[Route("api/infrastructure")]
public sealed class InfrastructureController(LiroDbContext db, IConnectionMultiplexer redis, ILogger<InfrastructureController> logger, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("Development-only PostgreSQL and Valkey readiness. Production returns 404.")]
    [ProducesResponseType<InfrastructureResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<InfrastructureResponse>(503)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // This detailed diagnostic is a development aid. Production uses infrastructure monitoring.
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        var postgresOk = false;
        double? latency = null;
        try
        {
            postgresOk = await db.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PostgreSQL readiness check failed.");
        }

        try
        {
            latency = (await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken)).TotalMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Valkey readiness check failed.");
        }

        return StatusCode(
            postgresOk && latency is not null ? 200 : 503,
            new InfrastructureResponse(postgresOk ? "ok" : "error", latency is not null ? "ok" : "error", latency));
    }
}
