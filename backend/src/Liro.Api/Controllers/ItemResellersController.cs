using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items/{id:guid}/resellers")]
public sealed class ItemResellersController(ItemResellerService service) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("One public Roblox resale-offer page, cached for up to two minutes. Offers are individual listed copies, not a complete owner list or a count of all circulating copies. Unsupported items return Unavailable. No automatic cursor traversal.")]
    [ProducesResponseType<ItemResellerPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(502)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken, [FromQuery] string? cursor = null, [FromQuery] int limit = 10)
    {
        var page = await service.GetAsync(id, cursor, limit, cancellationToken);
        return page is null ? NotFound() : Ok(page);
    }
}
