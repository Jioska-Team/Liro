using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogBrowseRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Browse([FromQuery] int page = 1, [FromQuery] int pageSize = 24, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            return BadRequest(new ProblemDetails { Status = 400, Title = "La página debe estar entre 1 y 10000 y el tamaño entre 1 y 100." });
        var result = await repository.BrowseAsync(page, pageSize, cancellationToken);
        return Ok(new { items = result.Items.Select(ItemResponse.From).ToArray(), page, pageSize, hasMore = result.HasMore });
    }

    [HttpGet("bundles/{bundleId:long}/components")]
    public async Task<IActionResult> Components(long bundleId, CancellationToken cancellationToken)
    {
        if (bundleId <= 0) return BadRequest();
        var result = await repository.GetBundleContentsAsync(bundleId, cancellationToken);
        return result is null ? NotFound() : Ok(new
        {
            bundle = ItemResponse.From(result.Bundle),
            components = result.Components.Select(ItemResponse.From).ToArray(),
            pendingAssetIds = result.PendingAssetIds,
            relationshipsObservedAtUtc = result.Bundle.BundleComponentsObservedAtUtc
        });
    }

    [HttpGet("assets/{assetId:long}/bundles")]
    public async Task<IActionResult> Parents(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0) return BadRequest();
        var bundles = await repository.GetParentBundlesAsync(assetId, cancellationToken);
        return Ok(bundles.Select(ItemResponse.From).ToArray());
    }
}
