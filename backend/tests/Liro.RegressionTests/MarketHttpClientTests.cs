using System.Net;
using Liro.MarketWorker;
using Microsoft.Extensions.Configuration;

namespace Liro.RegressionTests;

internal static class MarketHttpClientTests
{
    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MarketProxySecrets:A:Username"] = "test-user",
        ["MarketProxySecrets:A:Password"] = "test-password",
        ["MarketProxySecrets:B:Username"] = "test-user",
        ["MarketProxySecrets:B:Password"] = "test-password"
    }).Build();

    public static void Register(RegressionSuite suite)
    {
        suite.Test("MarketClients_ReusesAndIsolatesRoutesAndAccounts", () =>
        {
            var handlers = new List<ProbeHandler>();
            using var pool = new MarketHttpClients(Configuration(), (_, _) => { var h = new ProbeHandler(); handlers.Add(h); return h; });
            using var first = pool.AcquireProxy(new("http://proxy.example:8080", "A"));
            using var same = pool.AcquireProxy(new("http://proxy.example:8080", "A"));
            using var account = pool.AcquireProxy(new("http://proxy.example:8080", "B"));
            using var route = pool.AcquireProxy(new("http://other.example:8080", "A"));
            using var direct = pool.AcquireDirect();
            using var directAgain = pool.AcquireDirect();
            Check.That(ReferenceEquals(first.Client, same.Client), "No reutiliza la misma ruta/cuenta");
            Check.That(!ReferenceEquals(first.Client, account.Client) && !ReferenceEquals(first.Client, route.Client), "Mezcla rutas o cuentas");
            Check.That(ReferenceEquals(direct.Client, directAgain.Client) && !ReferenceEquals(direct.Client, first.Client) && handlers.Count == 4, "Pool directo no aislado/reutilizado");
        });

        suite.Test("MarketClients_PreservesTransportPolicy", () =>
        {
            var credential = new NetworkCredential("test-user", "test-password");
            using var direct = (SocketsHttpHandler)MarketHttpClients.CreateHandler(null, null);
            using var proxy = (SocketsHttpHandler)MarketHttpClients.CreateHandler(new("http://proxy.example:8080", "A"), credential);
            Check.That(!direct.UseProxy && direct.Proxy is null && proxy.UseProxy, "Alteró selección de ruta");
            var configured = (WebProxy)proxy.Proxy!;
            Check.That(configured.Address == new Uri("http://proxy.example:8080") && ReferenceEquals(configured.Credentials, credential), "Alteró proxy/credenciales");
            foreach (var handler in new[] { direct, proxy })
                Check.That(!handler.UseCookies && !handler.AllowAutoRedirect && handler.PooledConnectionLifetime == TimeSpan.FromMinutes(2), "Alteró cookies/redirects/renovación DNS");
            using var pool = new MarketHttpClients(Configuration(), (_, _) => new ProbeHandler());
            using var lease = pool.AcquireDirect();
            using var proxyLease = pool.AcquireProxy(new("http://proxy.example:8080", "A"));
            using var standalone = MarketTransport.CreateProxyClient(new("http://proxy.example:8080", "A"), Configuration());
            foreach (var client in new[] { lease.Client, proxyLease.Client, standalone })
                Check.That(client.Timeout == TimeSpan.FromSeconds(30) && client.MaxResponseContentBufferSize == 8 * 1024 * 1024, "Alteró límites de transporte");
        });

        suite.AsyncTest("MarketClients_RotationPreservesInFlightRequest", async () =>
        {
            var config = Configuration();
            var handlers = new List<ProbeHandler>();
            var passwords = new List<string>();
            using var pool = new MarketHttpClients(config, (_, credential) =>
            {
                passwords.Add(credential!.Password);
                var h = new ProbeHandler { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                handlers.Add(h); return h;
            });
            var old = pool.AcquireProxy(new("http://proxy.example:8080", "A"));
            var pending = old.Client.GetAsync("https://unit.invalid/");
            config["MarketProxySecrets:A:Password"] = "rotated-test-password";
            using var current = pool.AcquireProxy(new("http://proxy.example:8080", "A"));
            Check.That(!ReferenceEquals(old.Client, current.Client) && passwords.SequenceEqual(new[] { "test-password", "rotated-test-password" }), "No rota snapshot de credenciales");
            Check.That(handlers[0].Disposals == 0 && !pending.IsCompleted, "Interrumpió solicitud anterior");
            handlers[0].Pending!.SetResult(new(HttpStatusCode.OK));
            using var response = await pending;
            Check.That(response.StatusCode == HttpStatusCode.OK, "Perdió respuesta en vuelo");
            old.Dispose(); old.Dispose();
            Check.That(handlers[0].Disposals == 1 && handlers[1].Disposals == 0, "Cierre no corresponde al último lease");
            config["MarketProxySecrets:A:Password"] = null;
            Check.Throws<MarketHttpException>(() => pool.AcquireProxy(new("http://proxy.example:8080", "A")));
        });

        suite.Test("MarketClients_CacheBoundEvictsWithoutClosingBorrowedClient", () =>
        {
            var handlers = new List<ProbeHandler>();
            using var pool = new MarketHttpClients(Configuration(), (_, _) => { var h = new ProbeHandler(); handlers.Add(h); return h; });
            var first = pool.AcquireProxy(new("http://proxy0.example:8080", "A"));
            for (var i = 1; i <= 32; i++)
            {
                using var lease = pool.AcquireProxy(new($"http://proxy{i}.example:8080", "A"));
            }
            Check.That(handlers.Count == 33 && handlers[0].Disposals == 0, "Evicción cerró un cliente prestado");
            first.Dispose();
            Check.That(handlers[0].Disposals == 1, "Cliente retirado no se libera");
            for (var i = 33; i < 100; i++)
            {
                using var lease = pool.AcquireProxy(new($"http://proxy{i}.example:8080", "A"));
            }
            Check.That(handlers.Count(h => h.Disposals == 0) == 32, "Cache crece sin límite");
            pool.Dispose();
            Check.That(handlers.All(h => h.Disposals == 1), "No libera clientes cacheados exactamente una vez");
        });

        suite.AsyncTest("MarketClients_OwnerDisposalWaitsForInFlightLease", async () =>
        {
            var handler = new ProbeHandler { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var pool = new MarketHttpClients(Configuration(), (_, _) => handler);
            var lease = pool.AcquireDirect();
            var pending = lease.Client.GetAsync("https://unit.invalid/");
            pool.Dispose(); pool.Dispose();
            Check.Throws<ObjectDisposedException>(() => pool.AcquireDirect());
            Check.Throws<ObjectDisposedException>(() => pool.AcquireProxy(new("http://proxy.example:8080", "A")));
            Check.That(handler.Disposals == 0 && !pending.IsCompleted, "Dispose del propietario cancela solicitud prestada");
            handler.Pending!.SetResult(new(HttpStatusCode.OK));
            using var response = await pending;
            lease.Dispose(); lease.Dispose();
            Check.That(response.StatusCode == HttpStatusCode.OK && handler.Disposals == 1, "No completa y libera el último lease");
        });

        suite.AsyncTest("MarketClients_RequestCancellationDoesNotDisposeSharedClient", async () =>
        {
            var handler = new ProbeHandler { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var pool = new MarketHttpClients(Configuration(), (_, _) => handler);
            using var first = pool.AcquireDirect();
            using var caller = new CancellationTokenSource();
            var pending = first.Client.GetAsync("https://unit.invalid/", caller.Token);
            caller.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(async () => { using var response = await pending; });
            handler.Pending = null;
            using var next = pool.AcquireDirect();
            using var successful = await next.Client.GetAsync("https://unit.invalid/");
            Check.That(ReferenceEquals(first.Client, next.Client) && successful.StatusCode == HttpStatusCode.OK && handler.Disposals == 0, "Cancelación destruye cliente compartido");
        });
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Pending?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }
}
