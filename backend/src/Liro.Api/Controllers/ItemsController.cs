using System.ComponentModel.DataAnnotations;
using Liro.Api.Contracts;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[ApiController]
[Route("api/v1/items")]
public sealed class ItemsController(ItemService itemService, ImportRobloxItemService importer, ItemResponseMapper mapper) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [EndpointDescription("Get a stored item by internal ID. This read does not import from Roblox.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await itemService.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
    }

    [HttpGet("by-asset/{assetId:long}")]
    [EndpointDescription("Get a stored asset by positive Roblox ID. 404 indicates local absence.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    public async Task<IActionResult> GetByAssetId(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Asset ID must be positive." });
        }

        var item = await itemService.GetByAssetIdAsync(assetId, cancellationToken);
        return item is null ? NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
    }

    [HttpGet("by-bundle/{bundleId:long}")]
    [EndpointDescription("Get a stored bundle by positive Roblox ID. 404 indicates local absence.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    public async Task<IActionResult> GetByBundleId(long bundleId, CancellationToken cancellationToken)
    {
        if (bundleId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Bundle ID must be positive." });
        }
        var item = await itemService.GetByBundleIdAsync(bundleId, cancellationToken);
        var response = item is null ? (IActionResult)NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
        return response;
    }
    // Compatibility route. New clients use POST /api/v1/catalog/imports.
    [HttpPost("import-bundle/{bundleId:long}")]
    [Authorize(Policy = "CatalogAdmin")]
    [EnableRateLimiting("catalog-writes")]
    [EndpointDescription("Compatibility import route. New clients use POST /api/v1/catalog/imports. Requires the administrative key over HTTPS.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(409, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(502)]
    public async Task<IActionResult> ImportBundle(long bundleId, CancellationToken cancellationToken)
    {
        if (bundleId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Bundle ID must be positive." });
        }
        var item = await importer.ImportAsync(new Liro.Domain.Catalog.Models.CatalogItemKey(bundleId, CatalogItemType.Bundle), cancellationToken);
        var response = item is null ? (IActionResult)NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
        return response;
    }
    // Compatibility route. Keep the original status codes, authorization and shared write budget.
    [HttpPost("import/{assetId:long}")]
    [Authorize(Policy = "CatalogAdmin")]
    [EnableRateLimiting("catalog-writes")]
    [EndpointDescription("Compatibility import route. New clients use POST /api/v1/catalog/imports. Requires the administrative key over HTTPS.")]
    [ProducesResponseType<ItemResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(409, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(502)]
    public async Task<IActionResult> Import(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Asset ID must be positive." });
        }

        var item = await importer.ImportAsync(assetId, cancellationToken);
        return item is null ? NotFound() : Ok(await mapper.MapAsync(item, cancellationToken));
    }

    [HttpPost]
    [Authorize(Policy = "CatalogAdmin")]
    [EnableRateLimiting("catalog-writes")]
    [EndpointDescription("Create a stored asset. Returns Location for the created item; duplicate asset IDs return 409. Administrative HTTPS only.")]
    [ProducesResponseType<ItemResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(409, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(415, "application/problem+json")]
    public async Task<IActionResult> Create(CreateItemRequest request, CancellationToken cancellationToken)
    {
        var item = await itemService.CreateAsync(request.RobloxAssetId, request.Name, request.CollectibleItemId, request.MarketStatus, cancellationToken);
        return item is null ? Conflict(new ProblemDetails { Status = 409, Title = "The Roblox asset already exists." }) : CreatedAtAction(nameof(GetById), new
        {
            id = item.Id
        }, await mapper.MapAsync(item, cancellationToken));
    }
}

public sealed record CreateItemRequest([Range(typeof(long), "1", "9223372036854775807")] long RobloxAssetId, [Required, StringLength(200, MinimumLength = 1)] string Name, Guid? CollectibleItemId, [EnumDataType(typeof(ItemMarketStatus))] ItemMarketStatus MarketStatus);

