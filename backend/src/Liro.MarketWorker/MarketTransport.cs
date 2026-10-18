using System.Net;
using System.Security.Cryptography;
using System.Text;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Liro.MarketWorker;

public sealed class MarketHttpException(string code, TimeSpan delay, bool limited = false) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan Delay { get; } = delay;
    public bool Limited { get; } = limited;
}

public sealed class MarketTransport(IConfiguration configuration, IConnectionMultiplexer redis, IOptions<MarketOptions> settings)
{
    private readonly Dictionary<string, RedisRobloxRequestBudget> budgets = [];
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
        // Validate credentials before reserving capacity; never fall back to direct networking.
        using var http = CreateProxyClient(proxy, configuration);
        var cooldown = await redis.GetDatabase().KeyTimeToLiveAsync(CooldownKey);
        if (cooldown > TimeSpan.Zero) throw new MarketHttpException("EsperaProveedor", cooldown.Value, true);
        var normalized = new Uri(proxy.Address).GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var route = Budget("liro:{market}:route:" + hash, proxy.RequestsPerMinute);
        var global = Budget("liro:{market}:global", settings.Value.RequestsPerMinute);
        var id = Guid.NewGuid().ToString("N");
        var acquiredRoute = false;
        var acquiredGlobal = false;
        try
        {
            var delay = await route.TryAcquireAsync(id, ct);
            if (delay > TimeSpan.Zero) throw new MarketHttpException("CupoConexion", delay);
            acquiredRoute = true;
            delay = await global.TryAcquireAsync(id, ct);
            if (delay > TimeSpan.Zero) throw new MarketHttpException("CupoMercado", delay);
            acquiredGlobal = true;
            using var response = await http.GetAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{collectibleId:D}/resale-data", ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                delay = RetryDelay(response, DateTimeOffset.UtcNow);
                // A provider cooldown applies to the whole market pool; changing proxies cannot bypass it.
                await redis.GetDatabase().ScriptEvaluateAsync("""
                    local ttl = redis.call('PTTL', KEYS[1])
                    local requested = tonumber(ARGV[1])
                    if ttl < requested then redis.call('PSETEX', KEYS[1], requested, '1') end
                    return 1
                    """, [CooldownKey], [(long)Math.Ceiling(delay.TotalMilliseconds)]);
                throw new MarketHttpException("HTTP429", delay, true);
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

    public static TimeSpan RetryDelay(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromMinutes(1));
        return delay > TimeSpan.FromSeconds(3) ? delay : TimeSpan.FromSeconds(3);
    }

    private RedisRobloxRequestBudget Budget(string key, int capacity)
    {
        // Configuration reloads must not clear Redis accounting.
        var cacheKey = key + ":" + capacity;
        if (!budgets.TryGetValue(cacheKey, out var value))
        {
            if (budgets.Count > 128) budgets.Clear();
            budgets[cacheKey] = value = new(redis, Options.Create(new RobloxRequestOptions
            {
                BudgetKey = key, BudgetCapacity = capacity, BudgetWindow = TimeSpan.FromMinutes(1), MinimumInterval = TimeSpan.FromSeconds(3)
            }));
        }
        return value;
    }
}
