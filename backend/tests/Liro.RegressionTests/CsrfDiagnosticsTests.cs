using System.Net;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Logging;
namespace Liro.RegressionTests;

internal static class CsrfDiagnosticsTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var mode in new[] { "normal", "recuperado", "sin-token", "token-vacio", "definitivo", "otro-error", "red", "cancelado" })
            suite.AsyncTest("Diagnóstico 403: " + mode, async () =>
            {
                var calls = 0;
                var log = new Capture();
                string? body = null;
                using var cancellation = new CancellationTokenSource();
                using var http = new HttpClient(new Handler(async (request, ct) =>
                {
                    calls++;
                    var current = await request.Content!.ReadAsStringAsync(ct);
                    if (calls == 1)
                        body = current;
                    else
                        Check.That(current == body, "El reintento cambió el lote");
                    if (mode == "normal")
                        return Ok();
                    if (calls == 1)
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        if (mode != "sin-token")
                            response.Headers.TryAddWithoutValidation("x-csrf-token", mode == "token-vacio" ? " " : "token-secreto");
                        return response;
                    }
                    await Task.Delay(25, ct);
                    if (mode == "red")
                        throw new HttpRequestException("dato-privado");
                    if (mode == "cancelado")
                    {
                        cancellation.Cancel();
                        throw new OperationCanceledException("dato-privado", ct);
                    }
                    return mode == "recuperado" ? Ok() : new HttpResponseMessage(mode == "definitivo" ? HttpStatusCode.Forbidden : HttpStatusCode.InternalServerError);
                }))
                {
                    BaseAddress = new("https://catalog.roblox.com/")
                };
                try
                {
                    await new RobloxCatalogClient(http, logger: log).GetItemAsync(1, cancellation.Token);
                    Check.That(mode is "normal" or "recuperado", "Se ocultó el error");
                }
                catch (HttpRequestException) { Check.That(mode is not ("normal" or "recuperado"), "Falló una consulta correcta"); }
                catch (OperationCanceledException) { Check.That(mode == "cancelado", "Cancelación inesperada"); }
                Check.That(calls == (mode is "normal" or "sin-token" or "token-vacio" ? 1 : 2), "Cambió el número de solicitudes");
                if (mode == "normal")
                {
                    Check.That(log.Events.Count == 0, "Se registró un 403 inexistente");
                    return;
                }
                var e = log.Events.Single();
                var expected = mode switch
                {
                    "recuperado" => "Recuperado",
                    "cancelado" => "Cancelado",
                    "sin-token" or "token-vacio" or "definitivo" => "Definitivo",
                    _ => "ReintentoFallido"
                };
                Check.That((string)e["Outcome"]! == expected, "Clasificación incorrecta");
                Check.That(Convert.ToBoolean(e["Retried"]) == (calls == 2), "Indicador de reintento incorrecto");
                Check.That(Convert.ToDouble(e["ElapsedMilliseconds"]) >= (calls == 2 ? 20 : 0), "No midió la espera del reintento");
                Check.That(!log.Text.Contains("token-secreto") && !log.Text.Contains("dato-privado"), "Se filtraron secretos");
            });
    }
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":1,"name":"Artículo","itemType":"Asset","itemRestrictions":[],"creatorTargetId":1,"assetType":8}]}""") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Capture : ILogger<RobloxCatalogClient>
    {
        public List<Dictionary<string, object?>> Events { get; } = []; public string Text = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            Events.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value));
            Text += format(state, error);
        }
    }
}

