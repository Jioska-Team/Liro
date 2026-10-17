using System.Net;
using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class MainScanPriorityTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("El reintento respeta el plazo de Roblox sin añadir un minuto", async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(150);
            await WaitForProvider(deadline, cancel.Token);
            Check.That(DateTimeOffset.UtcNow >= deadline.AddMilliseconds(-5), "Se reintentó antes del plazo indicado");
        });
        suite.AsyncTest("Un plazo de Roblox vencido permite reanudar inmediatamente", async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitForProvider(DateTimeOffset.UtcNow.AddSeconds(-1), cancel.Token);
        });
        suite.AsyncTest("Un plazo largo no se acorta y permite cancelar la espera", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var pending = WaitForProvider(DateTimeOffset.UtcNow.AddMinutes(2), cancel.Token);
            Check.That(!pending.IsCompleted, "Se ignoró el plazo largo de Roblox");
            cancel.Cancel();
            try { await pending; throw new Exception("Se ignoró la cancelación"); }
            catch (OperationCanceledException) { }
        });
        suite.AsyncTest("La página siguiente puede competir durante la espera sin acortar el intervalo", async () =>
        {
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.FromMilliseconds(80) }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var times = new List<long>();
            Task<HttpResponseMessage> Send(string name) => gate.SendAsync(ct =>
            { order.Add(name); times.Add(System.Diagnostics.Stopwatch.GetTimestamp()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            var initial = gate.SendAsync(async ct => { await release.Task; return new(HttpStatusCode.OK); }, default);
            Task<HttpResponseMessage> known;
            using (new RobloxRequestPriority()) known = Send("conocidos");
            release.SetResult();
            (await initial).Dispose();
            Task<HttpResponseMessage> page;
            using (Principal()) page = Send("principal");
            foreach (var response in await Task.WhenAll(known, page)) response.Dispose();
            Check.That(order.SequenceEqual(new[] { "principal", "conocidos" }), "El turno se reservó antes de terminar la espera");
            Check.That(System.Diagnostics.Stopwatch.GetElapsedTime(times[0], times[1]).TotalMilliseconds >= 75,
                "Se redujo el intervalo entre solicitudes");
        });
        suite.AsyncTest("La búsqueda principal avanza sin bloquear conocidos ni complementarias", async () =>
        {
            using var gate = Gate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var jobs = new List<Task<HttpResponseMessage>>
            {
                gate.SendAsync(async ct => { await release.Task; return new(HttpStatusCode.OK); }, default)
            };
            Task<HttpResponseMessage> Add(string name) => gate.SendAsync(ct =>
            { order.Add(name); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            for (var i = 0; i < 12; i++)
            {
                using (new RobloxRequestPriority()) jobs.Add(Add("conocidos"));
                jobs.Add(Add("complementarias"));
                using (Principal()) jobs.Add(Add("principal"));
            }
            using (RobloxRequestPriority.Background()) jobs.Add(Add("imagen"));
            release.SetResult();
            foreach (var response in await Task.WhenAll(jobs)) response.Dispose();
            Check.That(order.Take(4).SequenceEqual(new[] { "principal", "principal", "conocidos", "complementarias" }),
                "La principal no recibe dos turnos o se bloquean las otras revisiones");
            Check.That(order.Count(x => x == "principal") == 12 && order.Count(x => x == "conocidos") == 12
                && order.Count(x => x == "complementarias") == 12 && order.Contains("imagen"), "Se perdió trabajo pendiente");
        });
        suite.AsyncTest("Las imágenes esperan durante la búsqueda principal pero conservan un turno acotado", async () =>
        {
            using var gate = Gate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var jobs = new List<Task<HttpResponseMessage>> { gate.SendAsync(async ct => { await release.Task; return new(HttpStatusCode.OK); }, default) };
            using var scan = Principal();
            using (RobloxRequestPriority.Background()) jobs.Add(gate.SendAsync(ct => { order.Add("imagen"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default));
            for (var i = 0; i < 30; i++) jobs.Add(gate.SendAsync(ct => { order.Add("principal"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default));
            release.SetResult();
            foreach (var response in await Task.WhenAll(jobs)) response.Dispose();
            Check.That(order.IndexOf("imagen") == 24, "Las imágenes interrumpen demasiado o quedan bloqueadas");
        });
        suite.AsyncTest("Cancelar una página no bloquea turnos y salir del recorrido restaura la prioridad", async () =>
        {
            using var gate = Gate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initial = gate.SendAsync(async ct => { await release.Task; return new(HttpStatusCode.OK); }, default);
            using var cancel = new CancellationTokenSource();
            Task<HttpResponseMessage> canceled;
            using (Principal()) canceled = gate.SendAsync(ct => throw new Exception("Se ejecutó una página cancelada"), cancel.Token);
            cancel.Cancel();
            var order = new List<string>();
            var ordinary = gate.SendAsync(ct => { order.Add("ordinaria"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            Task<HttpResponseMessage> known;
            using (new RobloxRequestPriority()) known = gate.SendAsync(ct => { order.Add("conocidos"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            release.SetResult();
            try { await canceled; throw new Exception("No se canceló"); } catch (OperationCanceledException) { }
            foreach (var response in await Task.WhenAll(initial, ordinary, known)) response.Dispose();
            Check.That(order.SequenceEqual(new[] { "conocidos", "ordinaria" }), "No se restauró la prioridad o quedó bloqueada la cola");
        });
    }
    private static IDisposable Principal() => RobloxRequestPriority.MainScan();
    private static Task WaitForProvider(DateTimeOffset deadline, CancellationToken token)
    {
        var type = typeof(Liro.Workers.Catalog.CatalogLimitedMonitor).Assembly.GetType("Liro.Workers.Catalog.CatalogWorkerDelay")!;
        return (Task)type.GetMethod("WaitForRateLimitAsync")!.Invoke(null,
            new object[] { new Liro.Application.RobloxIntegration.RobloxRateLimitedException(deadline), TimeSpan.FromMinutes(1), NullLogger.Instance, token })!;
    }
    private static RobloxRequestGate Gate() => new(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
        TimeProvider.System, NullLogger<RobloxRequestGate>.Instance);
}
