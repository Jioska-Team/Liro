using System.Net;
using System.Security.Cryptography;
using System.Text;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Liro.MarketWorker;

public sealed class MarketHttpException(string code, TimeSpan delay, bool limited = false) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan Delay { get; } = delay;
    public bool Limited { get; } = limited;
}

public sealed class MarketTransport(IConfiguration configuration, IConnectionMultiplexer redis, IOptions<MarketOptions> settings,
    ILogger<MarketTransport>? logger = null)
{
    private readonly ConcurrentDictionary<string, RedisRobloxRequestBudget> budgets = new();
    private readonly MarketDispatchGate dispatch = new();
    private const string CooldownKey = "liro:{market}:cooldown";

    public static HttpClient CreateProxyClient(MarketProxy proxy, IConfiguration configuration)
    {
        var section = configuration.GetSection("MarketProxySecrets:" + proxy.SecretReference);
        var username = section["Username"];
        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new MarketHttpException("CredencialesNoConfiguradas", TimeSpan.FromMinutes(5));
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(new Uri(proxy.Address), false) { Credentials = new NetworkCredential(username, password) },
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    }

    public async Task<ResaleObservation> ReadAsync(MarketProxy proxy, Guid collectibleId, CancellationToken ct)
    {
        // This method never changes route. The worker selects fallback only after all proxies are unavailable.
        using var http = CreateProxyClient(proxy, configuration);
        var normalized = MarketProxy.RouteKey(proxy.Address);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var route = Budget("liro:{market}:route:" + hash, proxy.RequestsPerMinute, TimeSpan.FromSeconds(3));
        var global = Budget("liro:{market}:global", settings.Value.RequestsPerMinute, TimeSpan.Zero);
        var id = Guid.NewGuid().ToString("N");
        var acquiredRoute = false;
        var acquiredGlobal = false;
        try
        {
            using var response = await dispatch.RunAsync(TimeSpan.FromSeconds(60d / settings.Value.RequestsPerMinute), async () =>
            {
                var proxyCooldown = await ProxyCooldownAsync(proxy);
                if (proxyCooldown > TimeSpan.Zero) throw new MarketHttpException("EsperaProxyHTTP402", proxyCooldown, true);
                var cooldown = await redis.GetDatabase().KeyTimeToLiveAsync(CooldownKey);
                if (cooldown > TimeSpan.Zero) throw new MarketHttpException("EsperaProveedor", cooldown.Value, true);
                var wait = await route.TryAcquireAsync(id, ct);
                if (wait > TimeSpan.Zero) throw new MarketHttpException("CupoConexion", wait);
                acquiredRoute = true;
                wait = await global.TryAcquireAsync(id, ct);
                if (wait > TimeSpan.Zero) throw new MarketHttpException("CupoMercado", wait);
                acquiredGlobal = true;
                logger?.LogInformation("Solicitud de mercado: ruta {Route}; artículo {Item}; presupuesto {Budget} por minuto.", hash[..12], collectibleId, settings.Value.RequestsPerMinute);
                return SendProxyAsync(http, proxy, collectibleId, ct);
            }, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var delay = RetryDelay(response, DateTimeOffset.UtcNow);
                // A provider cooldown applies to the whole market pool; changing proxies cannot bypass it.
                await redis.GetDatabase().ScriptEvaluateAsync("""
                    local ttl = redis.call('PTTL', KEYS[1])
                    local requested = tonumber(ARGV[1])
                    if ttl < requested then redis.call('PSETEX', KEYS[1], requested, '1') end
                    return 1
                    """, [CooldownKey], [(long)Math.Ceiling(delay.TotalMilliseconds)]);
                throw new MarketHttpException("HTTP" + (int)response.StatusCode, delay, response.StatusCode == HttpStatusCode.TooManyRequests);
            }
            if (!response.IsSuccessStatusCode) throw new MarketHttpException("HTTP" + (int)response.StatusCode, TimeSpan.FromMinutes(1));
            return ResaleObservation.Parse(await response.Content.ReadAsStringAsync(ct), DateTime.UtcNow);
        }
        finally
        {
            // Keep ambiguous failures conservative. Redis failure stops further sends.
            try
            {
                if (acquiredGlobal) await global.CompleteAsync(id, CancellationToken.None);
            }
            finally
            {
                if (acquiredRoute) await route.CompleteAsync(id, CancellationToken.None);
            }
        }
    }

    private async Task<HttpResponseMessage> SendProxyAsync(HttpClient http, MarketProxy proxy, Guid collectibleId, CancellationToken ct)
    {
        try
        {
            return await MarketProxyFailure.SendAsync(
                () => http.GetAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{collectibleId:D}/resale-data", ct),
                delay => PauseProxyAccountAsync(proxy, delay));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && ex.HttpRequestError is
            HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ProxyTunnelError)
        {
            await PauseProxyAccountAsync(proxy, TimeSpan.FromMinutes(2));
            throw new MarketHttpException("ProxySinConexion", TimeSpan.FromMinutes(2), true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await PauseProxyAccountAsync(proxy, TimeSpan.FromMinutes(2));
            throw new MarketHttpException("ProxyTimeout", TimeSpan.FromMinutes(2), true);
        }
    }

    public async Task<bool> ShouldUseDirectAsync(IReadOnlyList<MarketProxy> proxies)
    {
        if (!settings.Value.DirectFallbackEnabled) return false;
        var unavailable = 0;
        foreach (var proxy in proxies)
        {
            var secrets = configuration.GetSection("MarketProxySecrets:" + proxy.SecretReference);
            if (string.IsNullOrWhiteSpace(secrets["Username"]) || string.IsNullOrWhiteSpace(secrets["Password"])
                || await ProxyCooldownAsync(proxy) > TimeSpan.Zero) unavailable++;
        }
        var paused = await redis.GetDatabase().KeyTimeToLiveAsync(CooldownKey) > TimeSpan.Zero;
        return MarketFallbackPolicy.UseDirect(settings.Value.DirectFallbackEnabled, proxies.Count, unavailable, paused);
    }

    public async Task<ResaleObservation> ReadDirectAsync(Guid collectibleId, IReadOnlyList<MarketProxy> proxies, CancellationToken ct)
    {
        if (!await ShouldUseDirectAsync(proxies))
            throw new MarketHttpException("ProxyDisponibleOPausaProveedor", TimeSpan.FromSeconds(3), true);
        var requestSettings = new RobloxRequestOptions();
        configuration.GetSection("Roblox:Requests").Bind(requestSettings);
        // Fail closed on an unsupported budget configuration instead of silently creating another pool.
        if (requestSettings.BudgetCapacity is < 7 or > 18 || requestSettings.BudgetWindow != TimeSpan.FromMinutes(1))
            throw new MarketHttpException("PresupuestoDirectoInvalido", TimeSpan.FromMinutes(1), true);
        var direct = new MarketDirectBudget(redis, requestSettings, settings.Value.DirectRequestsPerMinute);
        var global = Budget("liro:{market}:global", settings.Value.RequestsPerMinute, TimeSpan.Zero);
        var id = Guid.NewGuid().ToString("N");
        var acquired = false;
        var marketAcquired = false;
        try
        {
            var wait = await direct.TryAcquireAsync(id, ct);
            if (wait > TimeSpan.Zero) throw new MarketHttpException("CupoDirectoPrioridadCatalogo", wait, true);
            acquired = true;
            wait = await global.TryAcquireAsync(id, ct);
            if (wait > TimeSpan.Zero) throw new MarketHttpException("CupoMercado", wait, true);
            marketAcquired = true;
            // Never let proxy settings or environment variables redirect the direct fallback.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
            ct.ThrowIfCancellationRequested();
            logger?.LogInformation("Solicitud de mercado: ruta DIRECTA_RESPALDO; artículo {Item}; presupuesto {Budget} por minuto.", collectibleId, settings.Value.DirectRequestsPerMinute);
            using var response = await http.GetAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{collectibleId:D}/resale-data", ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var delay = RetryDelay(response, DateTimeOffset.UtcNow);
                await redis.GetDatabase().ScriptEvaluateAsync("""
                    local requested = tonumber(ARGV[1])
                    if redis.call('PTTL', KEYS[1]) < requested then redis.call('PSETEX', KEYS[1], requested, '1') end
                    return 1
                    """, [CooldownKey], [(long)Math.Ceiling(delay.TotalMilliseconds)]);
                throw new MarketHttpException("HTTP" + (int)response.StatusCode, delay, true);
            }
            if (!response.IsSuccessStatusCode) throw new MarketHttpException("HTTP" + (int)response.StatusCode, TimeSpan.FromMinutes(1));
            return ResaleObservation.Parse(await response.Content.ReadAsStringAsync(ct), DateTime.UtcNow);
        }
        finally
        {
            try { if (marketAcquired) await global.CompleteAsync(id, CancellationToken.None); }
            finally { if (acquired) await direct.CompleteAsync(id, CancellationToken.None); }
        }
    }

    private static string ProxyCooldownKey(MarketProxy proxy)
        => "liro:{market}:proxy-account:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proxy.SecretReference))) + ":cooldown";

    public async Task<TimeSpan> ProxyCooldownAsync(MarketProxy proxy)
        => await redis.GetDatabase().KeyTimeToLiveAsync(ProxyCooldownKey(proxy)) ?? TimeSpan.Zero;

    private async Task PauseProxyAccountAsync(MarketProxy proxy, TimeSpan delay)
    {
        // Share account failures across its routes and restarts; do not reset rate-limit counters.
        await redis.GetDatabase().ScriptEvaluateAsync("""
            local ttl = redis.call('PTTL', KEYS[1])
            local requested = tonumber(ARGV[1])
            if ttl < requested then redis.call('PSETEX', KEYS[1], requested, '1') end
            return 1
            """, [ProxyCooldownKey(proxy)], [(long)Math.Ceiling(delay.TotalMilliseconds)]);
    }
    public static TimeSpan RetryDelay(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromMinutes(1));
        return delay > TimeSpan.FromSeconds(3) ? delay : TimeSpan.FromSeconds(3);
    }

    private RedisRobloxRequestBudget Budget(string key, int capacity, TimeSpan spacing)
    {
        // Configuration reloads must not clear Redis accounting.
        var cacheKey = key + ":" + capacity + ":" + spacing.Ticks;
        return budgets.GetOrAdd(cacheKey, _ => new(redis, Options.Create(new RobloxRequestOptions
            {
                BudgetKey = key, BudgetCapacity = capacity, BudgetWindow = TimeSpan.FromMinutes(1), MinimumInterval = spacing
            })));
    }
}

