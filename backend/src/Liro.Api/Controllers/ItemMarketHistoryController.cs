using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items/{id:guid}/market-history")]
public sealed class ItemMarketHistoryController(ItemService items, IItemMarketHistoryRepository history) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken, [FromQuery] DateTimeOffset? before = null, [FromQuery] int limit = 100)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Limit must be between 1 and 200." });
        }

        if (await items.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        var snapshots = await history.GetBeforeAsync(id, before?.UtcDateTime ?? DateTime.UtcNow, limit, cancellationToken);
        var response = snapshots.Select(x => ItemMarketResponse.From(x.Data, x.ObservedAtUtc, x.Source)).ToArray();
        return Ok(response);
    }
}
