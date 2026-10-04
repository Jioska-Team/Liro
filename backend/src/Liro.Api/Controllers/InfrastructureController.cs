using Liro.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/infrastructure")]
public sealed class InfrastructureController : ControllerBase
{
    private readonly LiroDbContext _dbContext;
    private readonly IConnectionMultiplexer _redis;

    public InfrastructureController(LiroDbContext dbContext, IConnectionMultiplexer redis)
    {
        _dbContext = dbContext;
        _redis = redis;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var postgresOk = await _dbContext.Database.CanConnectAsync();

        var database = _redis.GetDatabase();
        var latency = await database.PingAsync();

        return Ok(new
        {
            postgres = postgresOk ? "ok" : "error",
            valkey = "ok",
            valkeyLatencyMs = latency.TotalMilliseconds
        });
    }
}