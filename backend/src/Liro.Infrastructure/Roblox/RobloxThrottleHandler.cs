namespace Liro.Infrastructure.Roblox;

public sealed class RobloxThrottleHandler(RobloxRequestGate gate) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => gate.SendAsync(token => base.SendAsync(request, token), cancellationToken);
}
