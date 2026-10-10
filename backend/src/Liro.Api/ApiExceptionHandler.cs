using System.Text.Json;
using Liro.Application.Common;
using Liro.Application.RobloxIntegration;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Liro.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (status, title) = exception switch
        {
            RobloxRateLimitedException => (503, "Roblox is rate limiting requests. Retry after the indicated delay."),
            ConflictException => (409, "The item conflicts with existing or newer data."),
            ArgumentException => (400, "The supplied item data is invalid."),
            HttpRequestException or JsonException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException => (502, "The upstream provider is temporarily unavailable or returned invalid data."),
            NpgsqlException => (503, "The data service is temporarily unavailable."),
            _ => (500, "An unexpected error occurred.")
        };
        if (exception is RobloxRateLimitedException rateLimit)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(rateLimit.RemainingDelay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        logger.Log(
            status >= 500 && exception is not RobloxRateLimitedException ? LogLevel.Error : LogLevel.Warning,
            exception,
            "API request failed with status {StatusCode}, trace {TraceId}.",
            status,
            context.TraceIdentifier);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Extensions = { ["traceId"] = context.TraceIdentifier } },
            cancellationToken);
        return true;
    }
}
