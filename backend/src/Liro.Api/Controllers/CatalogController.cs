using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogBrowseRepository repository) : ControllerBase
{
    [HttpGet]
    [EndpointDescription("Stored visible catalog. page: 1-10000; pageSize: 1-100 (default 24).")]
    [ProducesResponseType<CatalogPageResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    public async Task<IActionResult> Browse([FromQuery] int page = 1, [FromQuery] int pageSize = 24, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            return BadRequest(new ProblemDetails { Status = 400, Title = "La página debe estar entre 1 y 10000 y el tamaño entre 1 y 100." });
        var result = await repository.BrowseAsync(page, pageSize, cancellationToken);
        return Ok(new CatalogPageResponse(result.Items.Select(ItemResponse.From).ToArray(), page, pageSize, result.HasMore));
    }

    [HttpGet("feed")]
    [EndpointDescription("Stored visible catalog in cursor order. pageSize: 1-100. Pass nextCursor unchanged as after; do not combine with page.")]
    [ProducesResponseType<CatalogFeedResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    public async Task<IActionResult> Feed([FromQuery] int pageSize = 24, [FromQuery] string? after = null, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100 || Request.Query.ContainsKey("page")
            || (Request.Query.ContainsKey("after") && string.IsNullOrEmpty(after))
            || !CatalogCursorCodec.TryDecode(after, out var cursor))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Usa pageSize entre 1 y 100 y el cursor after devuelto por esta ruta; no combines page con cursor." });
        var result = await repository.BrowseAfterAsync(cursor, pageSize, cancellationToken);
        var next = result.HasMore && result.Items.Count > 0
            ? CatalogCursorCodec.Encode(new CatalogCursor(result.Items[^1].RobloxCreatedAtUtc, result.Items[^1].Id)) : null;
        return Ok(new CatalogFeedResponse(result.Items.Select(ItemResponse.From).ToArray(), pageSize, result.HasMore, next));
    }

    [HttpGet("bundles/{bundleId:long}/components")]
    [EndpointDescription("Stored bundle components. Pending asset IDs have not been imported; 404 means the bundle is absent locally.")]
    [ProducesResponseType<BundleComponentsResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    public async Task<IActionResult> Components(long bundleId, CancellationToken cancellationToken)
    {
        if (bundleId <= 0)
            return BadRequest();
        var result = await repository.GetBundleContentsAsync(bundleId, cancellationToken);
        return result is null ? NotFound() : Ok(new BundleComponentsResponse(ItemResponse.From(result.Bundle),
            result.Components.Select(ItemResponse.From).ToArray(), result.PendingAssetIds,
            result.Bundle.BundleComponentsObservedAtUtc));
    }

    [HttpGet("assets/{assetId:long}/bundles")]
    [EndpointDescription("Stored parent bundles for a positive asset ID. No known parents returns an empty array.")]
    [ProducesResponseType<ItemResponse[]>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    public async Task<IActionResult> Parents(long assetId, CancellationToken cancellationToken)
    {
        if (assetId <= 0)
            return BadRequest();
        var bundles = await repository.GetParentBundlesAsync(assetId, cancellationToken);
        return Ok(bundles.Select(ItemResponse.From).ToArray());
    }
}
