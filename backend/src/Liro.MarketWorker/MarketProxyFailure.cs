using System.Net;
namespace Liro.MarketWorker;

public static class MarketProxyFailure
{
    public static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, Func<TimeSpan, Task> pauseAccount)
    {
        try { return await send(); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PaymentRequired)
        {
            // CONNECT failures are thrown before an HTTP response from Roblox exists.
            // Never attach the original exception: it can contain the proxy address or credentials.
            var delay = TimeSpan.FromMinutes(15);
            await pauseAccount(delay);
            throw new MarketHttpException("ProxyHTTP402", delay, limited: true);
        }
    }
}
