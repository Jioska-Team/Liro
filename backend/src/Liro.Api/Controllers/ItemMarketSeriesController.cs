using Liro.Application.Catalog.Repositories;
using Liro.Application.Catalog.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Liro.Api.Controllers;

[ApiController]
[Route("api/v1/items/{id:guid}/market-history")]
[EnableRateLimiting("market-history-reads")]
public sealed class ItemMarketSeriesController(ItemService items, IItemMarketSeriesRepository history) : ControllerBase
{
    [HttpGet("daily-sales")]
    [ProducesResponseType<DailySalesPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Daily(Guid id, CancellationToken cancellationToken,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null,
        [FromQuery] DateOnly? after = null, [FromQuery] int limit = 100)
    {
        var (start, end) = Range(from, to);
        if (!MarketSeriesRange.IsValid(start, end, limit))
            return InvalidRange();
        if (!await items.ExistsByIdAsync(id, cancellationToken))
            return NotFound();
        return Ok(await history.GetDailyAsync(id, start, end, after, limit, cancellationToken));
    }

    [HttpGet("rap")]
    [ProducesResponseType<RapHistoryPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Rap(Guid id, CancellationToken cancellationToken,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null,
        [FromQuery] DateTimeOffset? after = null, [FromQuery] int limit = 100)
    {
        var (start, end) = Range(from, to);
        if (!MarketSeriesRange.IsValid(start, end, limit))
            return InvalidRange();
        if (!await items.ExistsByIdAsync(id, cancellationToken))
            return NotFound();
        return Ok(await history.GetRapAsync(id, start, end, after, limit, cancellationToken));
    }

    private static (DateOnly, DateOnly) Range(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (from ?? today.AddDays(-29), to ?? today.AddDays(1));
    }

    private BadRequestObjectResult InvalidRange() => BadRequest(new ProblemDetails
    {
        Status = 400,
        Title = "Use limit 1–500 and from < to, with at most 366 days. Dates are UTC; to is exclusive."
    });
}
