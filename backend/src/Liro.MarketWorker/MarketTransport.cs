using System.Net;
using System.Security.Cryptography;
using System.Text;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Liro.RegressionTests")]

namespace Liro.MarketWorker;

public sealed class MarketHttpException(string code, TimeSpan delay, bool limited = false) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan Delay { get; } = delay;
    public bool Limited { get; } = limited;
}

public sealed class MarketTransport(IConfiguration configuration, IConnectionMultiplexer redis, IOptions<MarketOptions> settings,
    ILogger<MarketTransport>? logger = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, RedisRobloxRequestBudget> budgets = new();
    private readonly ConcurrentDictionary<string, MarketDirectBudget> directBudgets = new();
    private readonly MarketDispatchGate dispatch = new();
    private readonly MarketHttpClients clients = new(configuration);
    public void Dispose() { clients.Dispose(); dispatch.Dispose(); }
    private const string CooldownKey = "liro:{market}:cooldown";

    public static HttpClient CreateProxyClient(MarketProxy proxy, IConfiguration configuration)
    {
        var section = configuration.GetSection("MarketProxySecrets:" + proxy.SecretReference);
        var username = section["Username"];
        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new MarketHttpException("CredencialesNoConfiguradas", TimeSpan.FromMinutes(5));
        return MarketHttpClients.CreateClient(MarketHttpClients.CreateHandler(proxy, new NetworkCredential(username, password)));
    }

    public async Task<ResaleObservation> ReadAsync(MarketProxy proxy, Guid collectibleId, CancellationToken ct)
    {
        // This method never changes route. The worker selects fallback only after all proxies are unavailable.
        using var client = clients.AcquireProxy(proxy);
        var http = client.Client;
        var normalized = MarketProxy.RouteKey(proxy.Address);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var route = Budget("liro:{market}:route:" + hash, proxy.RequestsPerMinute, TimeSpan.FromSeconds(3));
        var global = Budget("liro:{market}:global", settings.Value.RequestsPerMinute, TimeSpan.Zero);
        var id = Guid.NewGuid().ToString("N");
        var acquiredRoute = false;
        var acquiredGlobal = false;
        CancellationTokenSource? admission = null;
        try
        {
            using var response = await dispatch.RunAsync(TimeSpan.FromSeconds(60d / settings.Value.RequestsPerMinute), async () =>
            {
                var proxyCooldown = await ProxyCooldownAsync(proxy);
                if (proxyCooldown > TimeSpan.Zero)
                    throw new MarketHttpException("EsperaProxyHTTP402", proxyCooldown, true);
                var cooldown = await redis.GetDatabase().KeyTimeToLiveAsync(CooldownKey);
                if (cooldown > TimeSpan.Zero)
                    throw new MarketHttpException("EsperaProveedor", cooldown.Value, true);
                var wait = await route.TryAcquireAsync(id, ct);
                if (wait > TimeSpan.Zero)
                    throw new MarketHttpException("CupoConexion", wait);
                acquiredRoute = true;
                wait = await global.TryAcquireAsync(id, ct);
                if (wait > TimeSpan.Zero)
                    throw new MarketHttpException("CupoMercado", wait);
                acquiredGlobal = true;
                admission = await RobloxRequestAdmission.CreateAsync(ct, (route, id), (global, id));
                logger?.LogInformation("Solicitud de mercado: ruta {Route}; artículo {Item}; presupuesto {Budget} por minuto.", hash[..12], collectibleId, settings.Value.RequestsPerMinute);
                return SendProxyAsync(http, proxy, collectibleId, admission.Token, ct);
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
            if (!response.IsSuccessStatusCode)
                throw new MarketHttpException("HTTP" + (int)response.StatusCode, TimeSpan.FromMinutes(1));
            return ResaleObservation.Parse(await response.Content.ReadAsStringAsync(ct), DateTime.UtcNow);
        }
        finally
        {
            admission?.Dispose();
            // Keep ambiguous failures conservative. Redis failure stops further sends.
            try
            {
                if (acquiredGlobal)
                    await global.CompleteAsync(id, CancellationToken.None);
            }
            finally
            {
                if (acquiredRoute)
                    await route.CompleteAsync(id, CancellationToken.None);
            }
        }
    }

    private async Task<HttpResponseMessage> SendProxyAsync(HttpClient http, MarketProxy proxy, Guid collectibleId, CancellationToken ct, CancellationToken callerToken)
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
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            await PauseProxyAccountAsync(proxy, TimeSpan.FromMinutes(2));
            throw new MarketHttpException("ProxyTimeout", TimeSpan.FromMinutes(2), true);
        }
    }

    public async Task<bool> ShouldUseDirectAsync(IReadOnlyList<MarketProxy> proxies)
    {
        if (!settings.Value.DirectFallbackEnabled)
            return false;
        var unavailable = 0;
        foreach (var proxy in proxies)
        {
            var secrets = configuration.GetSection("MarketProxySecrets:" + proxy.SecretReference);
            if (string.IsNullOrWhiteSpace(secrets["Username"]) || string.IsNullOrWhiteSpace(secrets["Password"])
                || await ProxyCooldownAsync(proxy) > TimeSpan.Zero)
                unavailable++;
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
        if (!MarketDirectBudget.IsValidCapacity(requestSettings, settings.Value.DirectRequestsPerMinute)
            || requestSettings.MinimumInterval < TimeSpan.FromSeconds(3))
            throw new MarketHttpException("PresupuestoDirectoInvalido", TimeSpan.FromMinutes(1), true);
        var directKey = requestSettings.BudgetKey + ":" + requestSettings.BudgetCapacity + ":"
            + requestSettings.MinimumInterval.Ticks + ":" + settings.Value.DirectRequestsPerMinute;
        var direct = directBudgets.GetOrAdd(directKey,
            _ => new MarketDirectBudget(redis, requestSettings, settings.Value.DirectRequestsPerMinute));
        var global = Budget("liro:{market}:global", settings.Value.RequestsPerMinute, TimeSpan.Zero);
        var id = Guid.NewGuid().ToString("N");
        var acquired = false;
        var marketAcquired = false;
        try
        {
            var wait = await direct.TryAcquireAsync(id, ct);
            if (wait > TimeSpan.Zero)
                throw new MarketHttpException("CupoDirectoPrioridadCatalogo", wait, true);
            acquired = true;
            wait = await global.TryAcquireAsync(id, ct);
            if (wait > TimeSpan.Zero)
                throw new MarketHttpException("CupoMercado", wait, true);
            marketAcquired = true;
            // Never let proxy settings or environment variables redirect the direct fallback.
            using var client = clients.AcquireDirect();
            var http = client.Client;
            ct.ThrowIfCancellationRequested();
            using var admission = await RobloxRequestAdmission.CreateAsync(ct, (direct, id), (global, id));
            logger?.LogInformation("Solicitud de mercado: ruta DIRECTA_RESPALDO; artículo {Item}; presupuesto {Budget} por minuto.", collectibleId, settings.Value.DirectRequestsPerMinute);
            using var response = await http.GetAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{collectibleId:D}/resale-data", admission.Token);
            await RobloxRateLimitResponse.PublishAsync(response, direct, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None);
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
            if (!response.IsSuccessStatusCode)
                throw new MarketHttpException("HTTP" + (int)response.StatusCode, TimeSpan.FromMinutes(1));
            return ResaleObservation.Parse(await response.Content.ReadAsStringAsync(ct), DateTime.UtcNow);
        }
        finally
        {
            try
            {
                if (marketAcquired)
                    await global.CompleteAsync(id, CancellationToken.None);
            }
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
            BudgetKey = key,
            BudgetCapacity = capacity,
            BudgetWindow = TimeSpan.FromMinutes(1),
            MinimumInterval = spacing
        })));
    }
}


// Retired clients stay alive until their last request finishes. Cache keys never contain secrets.
internal sealed class MarketHttpClients(IConfiguration configuration,
    Func<MarketProxy?, NetworkCredential?, HttpMessageHandler>? handlerFactory = null) : IDisposable
{
    private const int Capacity = 32;
    private readonly object sync = new();
    private readonly Dictionary<(string Route, string Reference), Entry> proxies = new();
    private Entry? direct;
    private bool disposed;

    public Lease AcquireDirect()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Borrow(direct ??= Create(null, null));
        }
    }

    public Lease AcquireProxy(MarketProxy proxy)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var section = configuration.GetSection("MarketProxySecrets:" + proxy.SecretReference);
            var username = section["Username"];
            var password = section["Password"];
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new MarketHttpException("CredencialesNoConfiguradas", TimeSpan.FromMinutes(5));
            var key = (MarketProxy.RouteKey(proxy.Address), proxy.SecretReference);
            if (proxies.TryGetValue(key, out var previous))
            {
                if (previous.Credentials!.UserName == username && previous.Credentials.Password == password)
                    return Borrow(previous);
                proxies.Remove(key);
                Retire(previous);
            }
            if (proxies.Count == Capacity)
            {
                var oldest = proxies.First();
                proxies.Remove(oldest.Key);
                Retire(oldest.Value);
            }
            var entry = Create(proxy, new NetworkCredential(username, password));
            proxies.Add(key, entry);
            return Borrow(entry);
        }
    }

    private Entry Create(MarketProxy? proxy, NetworkCredential? credentials) =>
        new(CreateClient((handlerFactory ?? CreateHandler)(proxy, credentials)), credentials);

    internal static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024
    };

    internal static HttpMessageHandler CreateHandler(MarketProxy? proxy, NetworkCredential? credentials) => new SocketsHttpHandler
    {
        UseProxy = proxy is not null,
        Proxy = proxy is null ? null : new WebProxy(new Uri(proxy.Address), false) { Credentials = credentials },
        UseCookies = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    private Lease Borrow(Entry entry)
    {
        entry.Users++;
        return new(entry.Client, () =>
        {
            lock (sync)
            {
                if (--entry.Users == 0 && entry.Retired)
                    entry.Client.Dispose();
            }
        });
    }

    private static void Retire(Entry entry)
    {
        entry.Retired = true;
        if (entry.Users == 0)
            entry.Client.Dispose();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (direct is not null) Retire(direct);
            foreach (var entry in proxies.Values) Retire(entry);
            proxies.Clear();
            direct = null;
        }
    }

    private sealed class Entry(HttpClient client, NetworkCredential? credentials)
    {
        public readonly HttpClient Client = client;
        public readonly NetworkCredential? Credentials = credentials;
        public int Users;
        public bool Retired;
    }

    internal sealed class Lease(HttpClient client, Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        public HttpClient Client { get; } = client;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }
}
