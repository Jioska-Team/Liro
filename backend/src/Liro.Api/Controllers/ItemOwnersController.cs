using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items/{id:guid}/owners")]
public sealed class ItemOwnersController(ItemOwnerService service) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("One public Roblox copy page ordered by user-asset ID, cached for up to two minutes. Owners may be redacted; this does not imply deletion. No full owner totals, Premium classification, acquisition dates, or automatic page traversal.")]
    [ProducesResponseType<ItemOwnerPage>(200)]
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
