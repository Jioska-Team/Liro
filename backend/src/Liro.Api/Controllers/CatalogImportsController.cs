using Liro.Api.Contracts;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/catalog/imports")]
[Authorize(Policy = "CatalogAdmin")]
[EnableRateLimiting("catalog-writes")]
public sealed class CatalogImportsController(ImportRobloxItemService importer) : ControllerBase
{
    /// <summary>Imports or refreshes one Roblox asset or bundle using its external identifier.</summary>
    [HttpPost]
    [ProducesResponseType<ItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Import([FromBody] ImportCatalogItemRequest request, CancellationToken cancellationToken)
    {
        // ApiController validates the body before invoking the importer.
        var type = request.ItemType == "Bundle" ? CatalogItemType.Bundle : CatalogItemType.Asset;
        var item = await importer.ImportAsync(new CatalogItemKey(request.RobloxId, type), cancellationToken);
        return item is null ? NotFound() : Ok(ItemResponse.From(item));
    }
}
