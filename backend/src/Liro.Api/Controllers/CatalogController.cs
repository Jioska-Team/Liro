using Liro.Api.Contracts;
using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Liro.Api.Controllers;

[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogBrowseRepository repository, ItemResponseMapper mapper) : ControllerBase
{
    [HttpGet("latest")]
    [EndpointDescription("Up to 30 visible official items ordered by registration in Liro, newest first, and 30 Limiteds. Confirmed conversions come first by detection time; initial imports with unknown conversion dates follow by registration. Equal timestamps sort by internal ID. Uses stored data only.")]
    [ProducesResponseType<LatestCatalogResponse>(200)]
    public async Task<IActionResult> Latest(CancellationToken cancellationToken)
    {
        var result = await repository.BrowseLatestAsync(cancellationToken);
        var mapped = await mapper.MapAsync(result.Items.Concat(result.Limiteds).ToArray(), cancellationToken);
        return Ok(new LatestCatalogResponse(mapped.Take(result.Items.Count).ToArray(), mapped.Skip(result.Items.Count).ToArray()));
    }

    [HttpGet]
    [EndpointDescription("Stored visible official catalog. page: 1-10000; pageSize: 1-100 (default 24). " + CatalogQuery.Description)]
    [ProducesResponseType<CatalogPageResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    public async Task<IActionResult> Browse([FromQuery] int page = 1, [FromQuery] int pageSize = 24, CancellationToken cancellationToken = default,
        [FromQuery] string? limited = null, [FromQuery] string? q = null, [FromQuery] string? sort = null, [FromQuery] string? direction = null)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            return BadRequest(new ProblemDetails { Status = 400, Title = "La página debe estar entre 1 y 10000 y el tamaño entre 1 y 100." });
        if (!CatalogQuery.TryParse(limited, q, sort, direction, out var options))
            return BadRequest(new ProblemDetails { Status = 400, Title = CatalogQuery.ValidationMessage });
        var result = options.IsDefault
            ? await repository.BrowseAsync(page, pageSize, cancellationToken)
            : await repository.BrowseAsync(page, pageSize, options, cancellationToken);
        return Ok(new CatalogPageResponse(await mapper.MapAsync(result.Items, cancellationToken), page, pageSize, result.HasMore));
    }

    [HttpGet("feed")]
    [EndpointDescription("Stored visible official catalog in cursor order. pageSize: 1-100. Pass nextCursor unchanged as after with the same filters and ordering; do not combine with page. " + CatalogQuery.Description)]
    [ProducesResponseType<CatalogFeedResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json", "application/json")]
    public async Task<IActionResult> Feed([FromQuery] int pageSize = 24, [FromQuery] string? after = null, CancellationToken cancellationToken = default,
        [FromQuery] string? limited = null, [FromQuery] string? q = null, [FromQuery] string? sort = null, [FromQuery] string? direction = null)
    {
        if (!CatalogQuery.TryParse(limited, q, sort, direction, out var options))
            return BadRequest(new ProblemDetails { Status = 400, Title = CatalogQuery.ValidationMessage });
        if (pageSize is < 1 or > 100 || Request.Query.ContainsKey("page")
            || (Request.Query.ContainsKey("after") && string.IsNullOrEmpty(after))
            || !CatalogCursorCodec.TryDecode(after, options, out var cursor))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Usa pageSize entre 1 y 100 y el cursor after devuelto por esta ruta; no combines page con cursor." });
        var result = options.IsDefault
            ? await repository.BrowseAfterAsync(cursor, pageSize, cancellationToken)
            : await repository.BrowseAfterAsync(cursor, pageSize, options, cancellationToken);
        var next = result.HasMore && result.Items.Count > 0
            ? CatalogCursorCodec.Encode(options.IsDefault
                ? new CatalogCursor(result.Items[^1].RobloxCreatedAtUtc, result.Items[^1].Id)
                : result.NextCursor ?? throw new InvalidOperationException("A filtered catalog page must include its continuation position."), options)
            : null;
        return Ok(new CatalogFeedResponse(await mapper.MapAsync(result.Items, cancellationToken), pageSize, result.HasMore, next));
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
        if (result is null)
            return NotFound();
        var items = await mapper.MapAsync(new[] { result.Bundle }.Concat(result.Components).ToArray(), cancellationToken);
        return Ok(new BundleComponentsResponse(items[0], items.Skip(1).ToArray(), result.PendingAssetIds,
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
        return Ok(await mapper.MapAsync(bundles, cancellationToken));
    }
}
