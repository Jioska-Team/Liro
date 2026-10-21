using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[ApiController]
[Route("api/v1/items/{id:guid}/market-summary")]
public sealed class ItemMarketSummaryController(IItemMarketSummaryRepository repository) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("Stored market summary. Missing measurements remain null; price and RAP have independent observation times.")]
    [ProducesResponseType<ItemMarketSummary>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var summary = await repository.GetAsync(id, cancellationToken);
        return summary is null ? NotFound() : Ok(summary);
    }
}
