using Liro.Infrastructure.Roblox;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

internal static class GlobalBudgetTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Un fallo del presupuesto impide solicitudes sin contabilizar", async () =>
        {
            var calls = 0;
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, new FailedBudget());
            await Check.ThrowsAsync<InvalidOperationException>(() => gate.SendAsync(ct =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }, default));
            Check.That(calls == 0, "Se envió tráfico sin presupuesto");
        });
        suite.AsyncTest("La puerta espera capacidad y cada envío consume una reserva", async () =>
        {
            var budget = new WaitingBudget();
            var calls = 0;
            using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, budget);
            using var response = await gate.SendAsync(ct => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, default);
            Check.That(calls == 1 && budget.Calls == 2 && budget.Completions == 1, "La puerta ignoró el presupuesto, no cerró la reserva o repitió el envío");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await gate.SendAsync(ct => throw new Exception("Se envió una consulta cancelada"), canceled.Token);
            }
            catch (OperationCanceledException) { }
            Check.That(budget.Calls == 2, "La cancelación consumió otra reserva");
        });
        suite.AsyncTest("Los errores y cancelaciones cierran la reserva sin liberar cupo prematuramente", async () =>
        {
            foreach (var canceled in new[] { false, true })
            {
                var budget = new WaitingBudget();
                using var gate = new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
                    TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, budget);
                using var cancel = new CancellationTokenSource();
                try
                {
                    await gate.SendAsync(ct =>
                    {
                        if (canceled)
                        {
                            cancel.Cancel();
                            throw new OperationCanceledException(ct);
                        }
                        throw new HttpRequestException("Fallo simulado");
                    }, cancel.Token);
                }
                catch (OperationCanceledException) when (canceled) { }
                catch (HttpRequestException) when (!canceled) { }
                Check.That(budget.Completions == 1, "La reserva quedó sin cerrar tras un error o cancelación");
            }
        });
        suite.AsyncTest("Los errores HTTP no generan reintentos ocultos fuera del presupuesto", async () =>
        {
            var calls = 0;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new RobloxRequestGate(Options.Create(new RobloxRequestOptions { MinimumInterval = TimeSpan.Zero }),
                TimeProvider.System, NullLogger<RobloxRequestGate>.Instance));
            services.AddTransient<RobloxThrottleHandler>();
            services.AddHttpClient("budget-test").ConfigurePrimaryHttpMessageHandler(() => new StubHttp(_ =>
            {
                calls++;
                return new(HttpStatusCode.ServiceUnavailable);
            })).WithRobloxTrafficControl();
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("budget-test");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var completed = false;
            try
            {
                using var response = await client.GetAsync("https://example.test/", cancel.Token);
                completed = response.StatusCode == HttpStatusCode.ServiceUnavailable;
            }
            catch (OperationCanceledException) { }
            Check.That(completed && calls == 1, "La resiliencia envió reintentos que no pasaron por el presupuesto");
        });
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_REDIS");
        suite.AsyncTest("Doce busquedas compartidas dejan disponible el presupuesto global", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var searchKey = key + ":search";
            await redis.GetDatabase().StringSetAsync(searchKey + ":mode", "completion-v2");
            try
            {
                var options = Settings(searchKey, 12, TimeSpan.FromMinutes(1), TimeSpan.Zero);
                var a = Budget(redis, options);
                var b = Budget(redis, options);
                var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Acquire(i % 2 == 0 ? a : b)));
                Check.That(results.Count(x => x == TimeSpan.Zero) == 12, "Se excedio el presupuesto compartido de busquedas");
                Check.That(await Acquire(Budget(redis, options)) > TimeSpan.Zero, "Otro cliente reinicio el presupuesto de busquedas");
                Check.That(await Acquire(Budget(redis, Settings(key, 18, TimeSpan.FromMinutes(1), TimeSpan.Zero))) == TimeSpan.Zero, "El presupuesto de busquedas bloqueo otros tipos de consulta");
            }
            finally
            {
                foreach (var prefix in new[] { key, searchKey })
                    await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { prefix, prefix + ":next", prefix + ":pending", prefix + ":mode", prefix + ":leases" });
            }
        });
        suite.AsyncTest("Un proceso terminado recupera su cupo sin borrar la ventana de seguridad", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            try
            {
                await redis.GetDatabase().SetAddAsync(key + ":pending", Environment.MachineName + "|2147483647|0|abandonada");
                var budget = Budget(redis, Settings(key, 1, TimeSpan.FromMilliseconds(500), TimeSpan.Zero));
                var delay = await Acquire(budget);
                Check.That(delay > TimeSpan.Zero && await redis.GetDatabase().SetLengthAsync(key + ":pending") == 0,
                    "No se recuperó el proceso terminado o se liberó el cupo sin conservar la ventana");
                await Task.Delay(delay.Add(TimeSpan.FromMilliseconds(30)));
                Check.That(await Acquire(budget) == TimeSpan.Zero, "El reinicio dejó el presupuesto bloqueado");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
        suite.AsyncTest("La actualización conserva el presupuesto anterior sin abrir cupos nuevos", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            try
            {
                var db = redis.GetDatabase();
                await db.StringSetAsync(key + ":mode", "legacy");
                var now = (long)await db.ScriptEvaluateAsync("local t=redis.call('TIME'); return tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)");
                await db.SortedSetAddAsync(key, Enumerable.Range(0, 18).Select(i => new SortedSetEntry(i.ToString(), now - 300)).ToArray());
                var budget = Budget(redis, Settings(key, 18, TimeSpan.FromMilliseconds(600), TimeSpan.Zero));
                var wait = await Acquire(budget);
                Check.That(wait > TimeSpan.FromMilliseconds(450), "La actualización reinició el contador o conservó fechas de reserva inseguras");
                Check.That(await db.SortedSetLengthAsync(key) == 18, "Se perdieron cupos vigentes");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
        suite.AsyncTest("Los envíos reales no superan el cupo aunque una reserva tarde en enviarse", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = Settings(key, 18, TimeSpan.FromMilliseconds(600), TimeSpan.Zero);
            var sent = new System.Collections.Concurrent.ConcurrentBag<long>();
            try
            {
                using var a = new RobloxRequestGate(Options.Create(options), TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, Budget(redis, options));
                using var b = new RobloxRequestGate(Options.Create(options), TimeProvider.System, NullLogger<RobloxRequestGate>.Instance, Budget(redis, options));
                var jobs = Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? a : b).SendAsync(async ct =>
                {
                    if (i == 0)
                        await Task.Delay(250, ct);
                    sent.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }, default)).ToArray();
                foreach (var response in await Task.WhenAll(jobs))
                    response.Dispose();
                var times = sent.Order().ToArray();
                Check.That(times.Length == 40, "No avanzaron los dos clientes");
                for (var i = 18; i < times.Length; i++)
                    Check.That(System.Diagnostics.Stopwatch.GetElapsedTime(times[i - 18], times[i]) >= options.BudgetWindow,
                        "Diecinueve envíos reales entraron en una misma ventana");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
        suite.AsyncTest("Una solicitud pendiente no libera cupo aunque venza la ventana de reserva", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            try
            {
                var a = Budget(redis, Settings(key, 1, TimeSpan.FromMilliseconds(150), TimeSpan.Zero));
                var id = Guid.NewGuid().ToString("N");
                Check.That(await a.TryAcquireAsync(id, default) == TimeSpan.Zero, "Primera reserva rechazada");
                await Task.Delay(220);
                Check.That(await Acquire(a) > TimeSpan.Zero, "Se liberó cupo antes de terminar la solicitud: permite adelantar envíos");
                await a.CompleteAsync(id, default);
                var delay = await Acquire(a);
                Check.That(delay > TimeSpan.FromMilliseconds(100), "La ventana no comienza después del cierre");
                await Task.Delay(delay.Add(TimeSpan.FromMilliseconds(30)));
                await a.CompleteAsync(id, default);
                Check.That(await Acquire(a) == TimeSpan.Zero, "El cierre duplicado prolongó la ventana o no se liberó capacidad");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
        suite.AsyncTest("El presupuesto global es atómico y se conserva entre clientes", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            var options = Settings(key, 18, TimeSpan.FromMinutes(1), TimeSpan.Zero);
            try
            {
                var a = Budget(redis, options);
                var b = Budget(redis, options);
                var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Acquire(i % 2 == 0 ? a : b)));
                Check.That(results.Count(x => x == TimeSpan.Zero) == 18, "Se superó el presupuesto compartido de 18 solicitudes");
                Check.That(await Acquire(Budget(redis, options)) > TimeSpan.Zero, "Un nuevo cliente reinició el contador global");
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                try
                {
                    await Acquire(a, canceled.Token);
                    throw new Exception("Se ignoró la cancelación");
                }
                catch (OperationCanceledException) { }
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
        suite.AsyncTest("El presupuesto libera capacidad al vencer la ventana y comparte la separación mínima", async () =>
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
            var key = "liro:prueba:presupuesto:" + Guid.NewGuid().ToString("N");
            // This fixture represents an already initialized budget; cold start has dedicated tests.
            await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
            try
            {
                var a = Budget(redis, Settings(key, 1, TimeSpan.FromMilliseconds(500), TimeSpan.Zero));
                Check.That(await Acquire(a) == TimeSpan.Zero, "Se rechazó la primera solicitud");
                var remaining = await Acquire(a);
                Check.That(remaining > TimeSpan.Zero, "La ventana permitió un exceso");
                await Task.Delay(remaining.Add(TimeSpan.FromMilliseconds(30)));
                Check.That(await Acquire(a) == TimeSpan.Zero, "No se recuperó capacidad al vencer la ventana");
                await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" });
                await redis.GetDatabase().StringSetAsync(key + ":mode", "completion-v2");
                var options = Settings(key, 18, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
                Check.That(await Acquire(Budget(redis, options)) == TimeSpan.Zero, "Se rechazó el primer cliente");
                Check.That(await Acquire(Budget(redis, options)) > TimeSpan.FromSeconds(2), "El segundo cliente ignoró los tres segundos compartidos");
            }
            finally { await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { key, key + ":next", key + ":pending", key + ":mode", key + ":leases" }); }
        });
    }

    private static RobloxRequestOptions Settings(string key, int capacity, TimeSpan window, TimeSpan interval) =>
        new()
        {
            MinimumInterval = interval,
            BudgetKey = key,
            BudgetCapacity = capacity,
            BudgetWindow = window
        };
    private static IRobloxRequestBudget Budget(IConnectionMultiplexer redis, RobloxRequestOptions options) => new RedisRobloxRequestBudget(redis, Options.Create(options));
    private static async Task<TimeSpan> Acquire(IRobloxRequestBudget budget, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var wait = await budget.TryAcquireAsync(id, ct);
        if (wait == TimeSpan.Zero)
            await budget.CompleteAsync(id, CancellationToken.None);
        return wait;
    }
    private sealed class FailedBudget : IRobloxRequestBudget
    {
        public Task CompleteAsync(string reservationId, CancellationToken ct) => Task.CompletedTask;
        public Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken ct) => throw new InvalidOperationException("Presupuesto no disponible");
    }
    private sealed class WaitingBudget : IRobloxRequestBudget
    {
        public int Calls, Completions;
        public Task CompleteAsync(string reservationId, CancellationToken ct)
        {
            Check.That(!ct.IsCancellationRequested, "No se debe cancelar el cierre de una reserva");
            Completions++;
            return Task.CompletedTask;
        }
        public Task<TimeSpan> TryAcquireAsync(string reservationId, CancellationToken ct) => Task.FromResult(++Calls == 1 ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero);
    }
}

