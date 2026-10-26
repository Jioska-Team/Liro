using Liro.Api.Contracts;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[ApiController]
[Route("api/v1/catalog/imports")]
[Authorize(Policy = "CatalogAdmin")]
[EnableRateLimiting("catalog-writes")]
public sealed class CatalogImportsController(ImportRobloxItemService importer, ItemResponseMapper mapper) : ControllerBase
{
    /// <summary>Imports or refreshes one Roblox asset or bundle using its external identifier.</summary>
    [HttpPost]
    [EndpointDescription("Import or refresh an Asset or Bundle by positive Roblox ID. Administrative HTTPS only.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(409, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(415, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(502)]
    public async Task<IActionResult> Import([FromBody] ImportCatalogItemRequest request, CancellationToken cancellationToken)
    {
        // ApiController validates the body before invoking the importer.
        var type = request.ItemType == "Bundle" ? CatalogItemType.Bundle : CatalogItemType.Asset;
        var item = await importer.ImportAsync(new CatalogItemKey(request.RobloxId, type), cancellationToken);
        return item is null ? NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
    }
}
