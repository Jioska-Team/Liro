using System.ComponentModel.DataAnnotations;
using Liro.Api.Contracts;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items")]
public sealed class ItemsController(ItemService itemService, ImportRobloxItemService importer) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await itemService.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(ItemResponse.From(item));
    }

    [HttpGet("by-asset/{assetId:long}")]
    public async Task<IActionResult> GetByAssetId(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Asset ID must be positive." });
        }

        var item = await itemService.GetByAssetIdAsync(assetId, cancellationToken);
        return item is null ? NotFound() : Ok(ItemResponse.From(item));
    }

    [HttpPost("import/{assetId:long}")]
    [Authorize(Policy = "CatalogAdmin")]
    [EnableRateLimiting("catalog-writes")]
    public async Task<IActionResult> Import(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Asset ID must be positive." });
        }

        var item = await importer.ImportAsync(assetId, cancellationToken);
        return item is null ? NotFound() : Ok(ItemResponse.From(item));
    }

    [HttpPost]
    [Authorize(Policy = "CatalogAdmin")]
    [EnableRateLimiting("catalog-writes")]
    public async Task<IActionResult> Create(CreateItemRequest request, CancellationToken cancellationToken)
    {
        var item = await itemService.CreateAsync(request.RobloxAssetId, request.Name, request.CollectibleItemId, request.MarketStatus, cancellationToken);
        return item is null ? Conflict(new ProblemDetails { Status = 409, Title = "The Roblox asset already exists." }) : CreatedAtAction(nameof(GetById), new
        {
            id = item.Id
        }, ItemResponse.From(item));
    }
}

public sealed record CreateItemRequest([Range(typeof(long), "1", "9223372036854775807")] long RobloxAssetId, [Required, StringLength(200, MinimumLength = 1)] string Name, Guid? CollectibleItemId, [EnumDataType(typeof(ItemMarketStatus))] ItemMarketStatus MarketStatus);
