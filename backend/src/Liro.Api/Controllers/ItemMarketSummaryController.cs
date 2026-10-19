using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items/{id:guid}/market-summary")]
public sealed class ItemMarketSummaryController(IItemMarketSummaryRepository repository) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ItemMarketSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var summary = await repository.GetAsync(id, cancellationToken);
        return summary is null ? NotFound() : Ok(summary);
    }
}
