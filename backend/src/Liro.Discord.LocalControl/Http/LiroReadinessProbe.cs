using System.Net.Http.Json;
using Liro.Discord.LocalControl.Hosting;

namespace Liro.Discord.LocalControl.Http;

public sealed class LiroReadinessProbe(HttpClient client) : IReadinessProbe
{
    public async Task<bool> IsReadyAsync(LanEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint.ApiBaseUri, "/api/lan/ready"), deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return false;
            }
            var content = await response.Content.ReadFromJsonAsync<ReadinessResponse>(deadline.Token);
            var ready = content?.Service == "liro-api" && content.Status == "ready";
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record ReadinessResponse(string Service, string Status);
}
