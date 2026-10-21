using System.Net;
using Liro.Application.RobloxIntegration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Liro.Infrastructure.Roblox;

public sealed class RobloxRequestOptions
{
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumFallbackCooldown { get; set; } = TimeSpan.FromMinutes(15);
    public int BudgetCapacity { get; set; } = 18;
    public int SearchBudgetCapacity { get; set; } = 12;
    public TimeSpan BudgetWindow { get; set; } = TimeSpan.FromMinutes(1);
    public string BudgetKey { get; set; } = "liro:{roblox}:request-budget:v1";
}

// Shared by catalog, discovery and thumbnails within this process, across HttpClient lifetimes.
public sealed class RobloxRequestGate(IOptions<RobloxRequestOptions> options, TimeProvider clock, ILogger<RobloxRequestGate> logger,
    IRobloxRequestBudget? budget = null, RobloxSearchBudget? searchBudget = null) : IDisposable
{
    private readonly RobloxRequestTurnstile mutex = new();
    private DateTimeOffset nextRequestAt;
    private DateTimeOffset retryAt;
    private int consecutiveLimits;
    public async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken, HttpRequestMessage? request = null)
    {
        await mutex.WaitAsync(cancellationToken);
        string? searchReservation = null;
        var deferredSearch = false;
        HttpResponseMessage? response = null;
        try
        {
            var now = clock.GetUtcNow();
            if (budget is not null && await budget.GetCooldownAsync(cancellationToken) is var sharedPause && sharedPause > TimeSpan.Zero)
                throw new RobloxRateLimitedException(now.Add(sharedPause));
            if (now < retryAt)
            {
                throw new RobloxRateLimitedException(retryAt);
            }

            if (now < nextRequestAt)
            {
                await Task.Delay(nextRequestAt - now, clock, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var reservationId = Guid.NewGuid().ToString("N");
            if (searchBudget is not null && request?.RequestUri is { IsAbsoluteUri: true } uri
                && uri.Host.Equals("catalog.roblox.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/v2/search/items/details", StringComparison.OrdinalIgnoreCase))
            {
                var wait = await searchBudget.Budget.TryAcquireAsync(reservationId, cancellationToken);
                if (wait > TimeSpan.Zero)
                {
                    deferredSearch = true;
                    logger.LogInformation("Presupuesto de búsquedas: {Operation} reintentará en {Seconds} segundos; el cupo global sigue disponible para otros tipos de consulta.", RobloxRequestPriority.OperationName, wait.TotalSeconds);
                    // Do not hold the shared queue while searches have no capacity.
                    // This is a local scheduling deadline, not a provider-wide cooldown.
                    throw new RobloxRateLimitedException(clock.GetUtcNow().Add(wait));
                }
                searchReservation = reservationId;
            }
            if (budget is not null)
            {
                while (true)
                {
                    var wait = await budget.TryAcquireAsync(reservationId, cancellationToken);
                    if (wait <= TimeSpan.Zero)
                        break;
                    logger.LogInformation("Presupuesto global de Roblox: próxima oportunidad en {DelaySeconds} segundos; se conserva el turno pendiente.", wait.TotalSeconds);
                    await Task.Delay(wait, clock, cancellationToken);
                }
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reservations = new List<(IRobloxRequestBudget Budget, string Id)>();
                if (budget is not null)
                    reservations.Add((budget, reservationId));
                if (searchReservation is not null)
                    reservations.Add((searchBudget!.Budget, searchReservation));
                using var admission = await RobloxRequestAdmission.CreateAsync(cancellationToken, reservations.ToArray());
                response = await send(admission.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    consecutiveLimits = Math.Min(consecutiveLimits + 1, 16);
                    var fallback = TimeSpan.FromTicks((long)Math.Min(options.Value.MaximumFallbackCooldown.Ticks,
                        options.Value.DefaultCooldown.Ticks * Math.Pow(2, consecutiveLimits - 1)));
                    var observed = clock.GetUtcNow();
                    // Keep the local safety pause even if publishing fails, and publish before releasing the reservation.
                    var header = response.Headers.RetryAfter;
                    var requested = header?.Delta ?? (header?.Date - observed);
                    retryAt = observed.Add(requested is { } value && value > TimeSpan.Zero ? value : fallback);
                    await RobloxRateLimitResponse.PublishAsync(response, budget, fallback, observed, CancellationToken.None);
                }
            }
            catch { response?.Dispose(); throw; }
            finally
            {
                if (budget is not null)
                {
                    // Close even on cancellation/error; keep capacity through 60 seconds after completion.
                    try
                    {
                        await budget.CompleteAsync(reservationId, CancellationToken.None);
                    }
                    catch { response?.Dispose(); throw; }
                }
            }
            now = clock.GetUtcNow();
            logger.LogInformation("Respuesta de Roblox: proceso {Operation}; ruta {Route}; estado {Status}; Retry-After {RetryAfter}; límite {Limit}; restante {Remaining}; reinicio {Reset}.",
                RobloxRequestPriority.OperationName, request?.RequestUri?.GetLeftPart(UriPartial.Path) ?? "sin ruta",
                (int)response.StatusCode, Header(response, "Retry-After"), Header(response, "x-ratelimit-limit"),
                Header(response, "x-ratelimit-remaining"), Header(response, "x-ratelimit-reset"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Dispose();
                logger.LogWarning("Roblox returned 429. Shared direct-IP traffic pauses until at least {RetryAtUtc}.", retryAt);
                throw new RobloxRateLimitedException(retryAt);
            }

            if (response.IsSuccessStatusCode)
            {
                consecutiveLimits = 0;
            }

            return response;
        }
        finally
        {
            try
            {
                if (searchReservation is not null)
                    await searchBudget!.Budget.CompleteAsync(searchReservation, CancellationToken.None);
            }
            catch { response?.Dispose(); throw; }
            finally
            {
                if (deferredSearch)
                    mutex.Release(); // No HTTP attempt; the previous spacing already elapsed.
                else
                {
                    nextRequestAt = clock.GetUtcNow().Add(options.Value.MinimumInterval);
                    mutex.ReleaseAfter(options.Value.MinimumInterval, clock);
                }
            }
        }
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return "no informado";
        var text = string.Join(",", values);
        return new string(text.Take(128).Where(c => !char.IsControl(c)).ToArray());
    }

    public void Dispose() => mutex.Dispose();
}
