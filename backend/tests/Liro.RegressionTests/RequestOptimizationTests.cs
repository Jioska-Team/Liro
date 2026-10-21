using System.Net;
using System.Text.Json;
using Liro.Infrastructure.Roblox;
using Liro.Domain.Catalog.Models;
using Liro.Domain.Catalog.Enums;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class RequestOptimizationTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("El contador registra cada consumidor adicional sin exponer el cursor", async () =>
        {
            var logger = new SessionLogger();
            var handler = new BlockingHttp();
            using var http = new HttpClient(handler) { BaseAddress = new("https://catalog.roblox.com/") };
            var session = new RobloxCatalogSession(TimeProvider.System, logger);
            var client = new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions()), session: session);
            Check.That(session.SharedQueryCount == 0, "El contador no empieza en cero");
            var jobs = Enumerable.Range(0, 25).Select(_ => client.GetPageAsync(Liro.Application.RobloxIntegration.Models.CatalogSearchScope.Collectibles, true, "cursor-confidencial", default)).ToArray();
            handler.Release.TrySetResult();
            await Task.WhenAll(jobs);
            Check.That(handler.Calls == 1 && session.SharedQueryCount == 24 && logger.Messages.Count == 24
                && logger.Messages.All(x => !x.Contains("cursor-confidencial") && x.Contains("Consulta compartida")), "El contador incluye originales, pierde uniones o expone parametros");
            await client.GetPageAsync(Liro.Application.RobloxIntegration.Models.CatalogSearchScope.Collectibles, true, "otro-cursor", default);
            Check.That(handler.Calls == 2 && session.SharedQueryCount == 24, "Una consulta independiente aumento el contador");
        });
        suite.AsyncTest("Una consulta compartida fallida no bloquea el siguiente intento", async () =>
        {
            var handler = new BlockingHttp { Fail = true };
            using var http = new HttpClient(handler) { BaseAddress = new("https://catalog.roblox.com/") };
            var session = new RobloxCatalogSession(TimeProvider.System);
            var client = new RobloxCatalogClient(http, session);
            var first = client.GetItemsAsync([new(1)]);
            var second = client.GetItemsAsync([new(1)]);
            handler.Release.TrySetResult();
            await Check.ThrowsAsync<HttpRequestException>(async () => await first);
            await Check.ThrowsAsync<HttpRequestException>(async () => await second);
            handler.Fail = false;
            var result = await client.GetItemsAsync([new(1)]);
            Check.That(handler.Calls == 2 && result[new(1)].Item is not null, "Se duplico el fallo o se reutilizo un resultado fallido");
        });
        suite.AsyncTest("Cancelar todos los consumidores cancela la consulta compartida", async () =>
        {
            var handler = new BlockingHttp();
            using var http = new HttpClient(handler) { BaseAddress = new("https://catalog.roblox.com/") };
            var client = new RobloxCatalogClient(http, new RobloxCatalogSession(TimeProvider.System));
            using var a = new CancellationTokenSource();
            using var b = new CancellationTokenSource();
            var first = client.GetItemsAsync([new(1)], a.Token);
            var second = client.GetItemsAsync([new(1)], b.Token);
            a.Cancel();
            b.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(async () => await first);
            await Check.ThrowsAsync<OperationCanceledException>(async () => await second);
            await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.That(handler.Calls == 1, "Se duplico la consulta compartida antes de cancelar");
        });
        suite.AsyncTest("Dos clientes comparten el mismo lote concurrente y cancelan por separado", async () =>
        {
            var handler = new BlockingHttp();
            using var http = new HttpClient(handler) { BaseAddress = new("https://catalog.roblox.com/") };
            var session = new RobloxCatalogSession(TimeProvider.System);
            using var cancel = new CancellationTokenSource();
            var first = new RobloxCatalogClient(http, session).GetItemsAsync([new(1), new(2)], cancel.Token);
            var second = new RobloxCatalogClient(http, session).GetItemsAsync([new(2), new(1)]);
            cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(async () => await first);
            handler.Release.TrySetResult();
            var result = await second;
            Check.That(handler.Calls == 1 && session.SharedQueryCount == 1 && result.Count == 2 && result.Values.All(x => x.Error is null), "Se duplico el lote, no se conto la union o la cancelacion afecto al otro cliente");
        });
        suite.AsyncTest("Las busquedas identicas se comparten sin retrasar la prioridad principal", async () =>
        {
            foreach (var main in new[] { false, true })
            {
                var handler = new BlockingHttp();
                using var http = new HttpClient(handler) { BaseAddress = new("https://catalog.roblox.com/") };
                var session = new RobloxCatalogSession(TimeProvider.System);
                var client = new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions()), session: session);
                var first = client.GetPageAsync(Liro.Application.RobloxIntegration.Models.CatalogSearchScope.Collectibles, true, null, default);
                Task<Liro.Application.RobloxIntegration.Models.RobloxCatalogDiscoveryPage> second;
                using (main ? RobloxRequestPriority.MainScan() : RobloxRequestPriority.Operation("otra busqueda"))
                    second = client.GetPageAsync(Liro.Application.RobloxIntegration.Models.CatalogSearchScope.Collectibles, true, null, default);
                handler.Release.TrySetResult();
                await Task.WhenAll(first, second);
                Check.That(handler.Calls == (main ? 2 : 1) && session.SharedQueryCount == (main ? 0 : 1), "La principal compartio un turno inferior, se duplico una busqueda o el contador es incorrecto");
            }
        });
        suite.Test("La reutilización de conocidos caduca al minuto y no amplía la caché general", () =>
        {
            var now = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
            var clock = new Clock { Now = now };
            var session = new RobloxCatalogSession(clock);
            session.Store(new(1, "Limited", null, ItemMarketStatus.Limited)
            {
                ObservedAtUtc = now.UtcDateTime,
                Details = new()
                {
                    CreatorId = 1,
                    CreatorType = "User"
                }
            });
            clock.Now = now.AddSeconds(20);
            Check.That(session.Find(new(1)) is null && session.FindRecent(new(1), TimeSpan.FromMinutes(1)) is not null,
                "Se amplió la vigencia general o se borró una observación todavía útil");
            clock.Now = now.AddSeconds(59);
            Check.That(session.FindRecent(new(1), TimeSpan.FromMinutes(1))!.ObservedAtUtc == now.UtcDateTime, "Se cambió la fecha original");
            clock.Now = now.AddSeconds(60);
            Check.That(session.FindRecent(new(1), TimeSpan.FromMinutes(1)) is null, "Se reutilizó una observación vencida");
        });
        suite.AsyncTest("Una búsqueda validada evita repetir inmediatamente la consulta de detalles", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                Check.That(request.Method == HttpMethod.Get, "Se repitieron los detalles ya consultados");
                return Json("""{"data":[{"id":1,"itemType":"Asset","name":"Artículo","assetType":8,"creatorTargetId":1,"creatorType":"User","itemCreatedUtc":"2026-01-01T00:00:00Z","itemRestrictions":["Limited"]}],"nextPageCursor":null}""");
            });
            var state = new RobloxCatalogSession(TimeProvider.System);
            var page = await new RobloxCatalogDiscoveryClient(http, Options.Create(new RobloxCatalogOptions()), session: state)
                .GetPageAsync(Liro.Application.RobloxIntegration.Models.CatalogSearchScope.Collectibles, true, null, default);
            var item = await new RobloxCatalogClient(http, state).GetItemAsync(1);
            Check.That(calls == 1 && item!.MarketStatus == ItemMarketStatus.Limited && item.ObservedAtUtc == page.Details[new(1)].Item!.ObservedAtUtc,
                "Se alteró la observación compartida");
        });
        suite.AsyncTest("El token se comparte entre clientes y se renueva una vez cuando Roblox lo rechaza", async () =>
        {
            var calls = 0;
            using var http = Http(request =>
            {
                calls++;
                var token = request.Headers.TryGetValues("x-csrf-token", out var values) ? values.Single() : null;
                if (calls == 1)
                {
                    Check.That(token is null, "Token inicial inesperado");
                    return Challenge("primero");
                }
                if (calls == 3)
                {
                    Check.That(token == "primero", "No se reutilizó el token");
                    return Challenge("segundo");
                }
                Check.That(token == (calls == 2 ? "primero" : "segundo"), "Token no renovado");
                return Details(request);
            });
            var state = new RobloxCatalogSession(TimeProvider.System);
            await new RobloxCatalogClient(http, state).GetItemAsync(1);
            await new RobloxCatalogClient(http, state).GetItemAsync(2);
            await new RobloxCatalogClient(http, state).GetItemAsync(3);
            Check.That(calls == 5, "Solicitudes de validación redundantes");
        });
        suite.AsyncTest("Los clientes comparten detalles recientes y solo solicitan los identificadores faltantes", async () =>
        {
            var calls = 0;
            var sizes = new List<int>();
            using var http = Http(request => { calls++; sizes.Add(Keys(request).Length); return Details(request); });
            var clock = new Clock();
            var state = new RobloxCatalogSession(clock);
            var first = await new RobloxCatalogClient(http, state).GetItemAsync(1);
            var results = await new RobloxCatalogClient(http, state).GetItemsAsync([new(1), new(2)]);
            Check.That(calls == 2 && sizes.SequenceEqual(new[] { 1, 1 }) && results[new(1)].Item!.ObservedAtUtc == first!.ObservedAtUtc,
                "Se repitió una consulta o se falsificó la fecha de observación");
            clock.Now = DateTimeOffset.UtcNow.AddSeconds(21);
            await new RobloxCatalogClient(http, state).GetItemAsync(1);
            Check.That(calls == 3, "No caducaron los datos recientes");
        });
        suite.AsyncTest("La caché distingue artículos y paquetes y nunca almacena omisiones", async () =>
        {
            var calls = 0;
            using var http = Http(request => { calls++; return calls == 1 ? Json("{\"data\":[]}") : Details(request); });
            var client = new RobloxCatalogClient(http, new RobloxCatalogSession(TimeProvider.System));
            await client.GetItemsAsync([new(1)]);
            await client.GetItemsAsync([new(1)]);
            await client.GetItemsAsync([new(1, CatalogItemType.Bundle)]);
            await client.GetItemsAsync([new(1), new(1, CatalogItemType.Bundle)]);
            Check.That(calls == 3, "Se mezclaron tipos o se almacenó un error");
        });
        suite.AsyncTest("Los limitados preceden a las miniaturas y estas conservan turnos", async () =>
        {
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var initial = gate.SendAsync(async ct => { await release.Task; return new(HttpStatusCode.OK); }, default);
            Task<HttpResponseMessage> low;
            using (RobloxRequestPriority.Background())
                low = gate.SendAsync(ct => { order.Add("miniatura"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            var jobs = new List<Task<HttpResponseMessage>> { initial, low };
            for (var i = 0; i < 10; i++)
            {
                using var priority = new RobloxRequestPriority();
                jobs.Add(gate.SendAsync(ct => { order.Add("limitado"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default));
            }
            jobs.Add(gate.SendAsync(ct => { order.Add("ordinaria"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default));
            release.SetResult();
            foreach (var response in await Task.WhenAll(jobs))
                response.Dispose();
            Check.That(order[0] == "limitado" && order.IndexOf("ordinaria") == 3 && order.IndexOf("miniatura") == 8,
                "Prioridad o equidad incorrectas");
        });
    }
    private sealed class SessionLogger : Microsoft.Extensions.Logging.ILogger<RobloxCatalogSession>
    {
        public List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private sealed class BlockingHttp : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            if (Fail)
                return new(HttpStatusCode.ServiceUnavailable);
            return request.Method == HttpMethod.Post ? Details(request) : Json("{\"data\":[],\"nextPageCursor\":null}");
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset? Now;
        public override DateTimeOffset GetUtcNow() => Now ?? DateTimeOffset.UtcNow;
    }
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> action) => new(new StubHttp(action)) { BaseAddress = new("https://catalog.roblox.com/") };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Challenge(string token)
    {
        var result = new HttpResponseMessage(HttpStatusCode.Forbidden);
        result.Headers.Add("x-csrf-token", token);
        return result;
    }
    private static CatalogItemKey[] Keys(HttpRequestMessage request)
    {
        using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(x => new CatalogItemKey(x.GetProperty("id").GetInt64(), (CatalogItemType)x.GetProperty("itemType").GetInt32())).ToArray();
    }
    private static HttpResponseMessage Details(HttpRequestMessage request) => Json(JsonSerializer.Serialize(new
    {
        data = Keys(request).Select(k => new { id = k.Id, itemType = k.Type.ToString(), name = "Artículo", itemRestrictions = Array.Empty<string>(), creatorTargetId = 1, creatorType = "User" })
    }));
}
