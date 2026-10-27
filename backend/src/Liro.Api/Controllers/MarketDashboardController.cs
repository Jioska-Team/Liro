using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/market/summary")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
public sealed class MarketDashboardController(IMarketDashboardRepository repository) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("Stored registered-Limited statistics. Volume is estimated for the previous complete UTC day; comparison uses only items present in both periods. Pulse classifies breadth over all valid items, including unchanged observations, subject to minimum freshness and coverage. Missing evidence is null. No requests to Roblox.")]
    [ProducesResponseType<MarketDashboardSummary>(200)]
    public async Task<ActionResult<MarketDashboardSummary>> Get(CancellationToken cancellationToken) =>
        Ok(await repository.GetLatestAsync(DateTime.UtcNow, cancellationToken));
}
