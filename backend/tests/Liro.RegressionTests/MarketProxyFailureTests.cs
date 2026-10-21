using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Liro.MarketWorker;
namespace Liro.RegressionTests;

internal static class MarketProxyFailureTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Proxy402_PausesAccount_WithoutLeakingCredentials", async () =>
        {
            var pauses = new List<TimeSpan>();
            var error = await Check.ThrowsAsync<MarketHttpException>(() => MarketProxyFailure.SendAsync(
             () => Task.FromException<HttpResponseMessage>(new HttpRequestException("proxy user:secret rejected", null, HttpStatusCode.PaymentRequired)),
             delay => { pauses.Add(delay); return Task.CompletedTask; }));
            Check.That(error.Code == "ProxyHTTP402" && error.Limited, "No distingue el rechazo del proveedor");
            Check.That(pauses.Count == 1 && pauses[0] == TimeSpan.FromMinutes(15) && error.Delay == pauses[0], "No pausa la cuenta durante 15 minutos");
            Check.That(!error.ToString().Contains("secret"), "Expone credenciales");
        });
        suite.AsyncTest("ProxySuccess_PreservesResponse_WithoutPause", async () =>
        {
            using var expected = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("original") };
            var pauses = 0;
            var actual = await MarketProxyFailure.SendAsync(() => Task.FromResult(expected), _ => { pauses++; return Task.CompletedTask; });
            Check.That(ReferenceEquals(actual, expected) && await actual.Content.ReadAsStringAsync() == "original" && pauses == 0, "Altera respuesta o pausa un éxito");
        });
        suite.AsyncTest("ProxyOtherFailure_IsNotMisclassifiedAs402", async () =>
        {
            var expected = new HttpRequestException("other", null, HttpStatusCode.BadGateway);
            var pauses = 0;
            var actual = await Check.ThrowsAsync<HttpRequestException>(() => MarketProxyFailure.SendAsync(() => Task.FromException<HttpResponseMessage>(expected), _ => { pauses++; return Task.CompletedTask; }));
            Check.That(ReferenceEquals(actual, expected) && pauses == 0, "Oculta un error distinto");
        });
        suite.AsyncTest("Proxy402_CooldownStorageFailure_StopsProcessing", async () =>
        {
            var expected = new InvalidOperationException("storage");
            var actual = await Check.ThrowsAsync<InvalidOperationException>(() => MarketProxyFailure.SendAsync(() => Task.FromException<HttpResponseMessage>(new HttpRequestException("402", null, HttpStatusCode.PaymentRequired)), _ => Task.FromException(expected)));
            Check.That(ReferenceEquals(actual, expected), "Ignora fallo de persistencia de la pausa");
        });

        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS");
        foreach (var stopRequested in new[] { false, true })
        {
            suite.AsyncTest(stopRequested ? "ProxyStop_DoesNotPauseAccount" : "ProxyAdmissionDeadline_PausesAccount", async () =>
            {
                using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
                var reference = "TEST_" + Guid.NewGuid().ToString("N");
                var proxy = new MarketProxy("http://127.0.0.1:9", reference);
                var transport = new MarketTransport(new ConfigurationBuilder().Build(), redis, Options.Create(new MarketOptions()));
                var key = "liro:{market}:proxy-account:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))) + ":cooldown";
                using var caller = new CancellationTokenSource();
                using var admission = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
                var sends = 0;
                using var http = new HttpClient(new StubHttp(_ =>
                {
                    sends++;
                    if (stopRequested)
                        caller.Cancel();
                    else
                        admission.Cancel();
                    throw new OperationCanceledException(admission.Token);
                }));
                var send = typeof(MarketTransport).GetMethod("SendProxyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    Task<HttpResponseMessage> Invoke() => (Task<HttpResponseMessage>)send.Invoke(transport,
                        [http, proxy, Guid.NewGuid(), admission.Token, caller.Token])!;
                    if (stopRequested)
                    {
                        var error = await Check.ThrowsAsync<OperationCanceledException>(() => Invoke());
                        Check.That(error.CancellationToken.IsCancellationRequested, "Stop no conserva la cancelación");
                        Check.That(!await redis.GetDatabase().KeyExistsAsync(key), "Stop pausa una cuenta disponible");
                    }
                    else
                    {
                        var error = await Check.ThrowsAsync<MarketHttpException>(() => Invoke());
                        Check.That(error.Code == "ProxyTimeout" && error.Limited && error.Delay == TimeSpan.FromMinutes(2),
                            "El plazo de admisión no se clasifica como timeout del proxy");
                        var cooldown = await transport.ProxyCooldownAsync(proxy);
                        Check.That(cooldown > TimeSpan.Zero && cooldown <= TimeSpan.FromMinutes(2),
                            "El timeout no persiste la pausa de la cuenta");
                        Check.That(!caller.IsCancellationRequested, "El plazo de admisión cancela el worker");
                    }
                    Check.That(sends == 1, "La cancelación provoca otra solicitud");
                }
                finally { await redis.GetDatabase().KeyDeleteAsync(key); }
            });
        }
    }
}
