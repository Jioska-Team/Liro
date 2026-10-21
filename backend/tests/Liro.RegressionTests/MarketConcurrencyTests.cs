using Liro.MarketWorker;
using System.Diagnostics;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Liro.Infrastructure.Persistence;

namespace Liro.RegressionTests;

internal static class MarketConcurrencyTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Mercado limita cinco consultas y no duplica una ruta ocupada", async () =>
        {
            var pool = new MarketLanePool(5);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = 0;
            async Task Work()
            {
                if (Interlocked.Increment(ref running) == 5)
                    entered.SetResult();
                await release.Task;
            }
            for (var i = 0; i < 5; i++)
                Check.That(pool.TryStart("ruta" + i, Work), "No inicia las cinco rutas");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check.That(!pool.TryStart("ruta0", Work), "Duplicó una ruta activa");
                Check.That(!pool.TryStart("ruta5", Work), "Superó cinco consultas");
            }
            finally { release.TrySetResult(); await pool.DrainAsync(); }
            Check.That(pool.TryStart("ruta0", () => Task.CompletedTask), "No reutiliza una ruta terminada");
            await pool.DrainAsync();
        });
        suite.AsyncTest("Mercado escalona salidas sin esperar la respuesta anterior", async () =>
        {
            using var gate = new MarketDispatchGate();
            var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = Stopwatch.StartNew();
            long first = 0, second = 0;
            var a = gate.RunAsync(TimeSpan.FromMilliseconds(100), () => { first = clock.ElapsedMilliseconds; firstStarted.SetResult(); return Task.FromResult(response.Task); }, default);
            await firstStarted.Task;
            var b = gate.RunAsync(TimeSpan.FromMilliseconds(100), () => { second = clock.ElapsedMilliseconds; secondStarted.SetResult(); return Task.FromResult(Task.FromResult(2)); }, default);
            try
            {
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check.That(!a.IsCompleted && await b == 2, "Serializa las respuestas");
                Check.That(second - first >= 90, "No respeta el espaciado de salida");
            }
            finally { response.TrySetResult(1); await a; }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var sent = false;
            try
            {
                await gate.RunAsync(TimeSpan.FromSeconds(1), () => { sent = true; return Task.FromResult(Task.FromResult(1)); }, canceled.Token);
            }
            catch (OperationCanceledException) { }
            Check.That(!sent, "Una salida cancelada envió una consulta");
        });
        suite.Test("Mercado identifica aliases de la misma dirección de proxy", () =>
        {
            Check.That(MarketProxy.RouteKey("http://PROXY.example:8080/") == MarketProxy.RouteKey("http://proxy.example:8080"), "Duplicó la ruta por alias");
            Check.That(MarketProxy.RouteKey("http://proxy.example:8080") != MarketProxy.RouteKey("http://proxy.example:8081"), "Confundió puertos distintos");
        });
        var connection = IntegrationPrerequisites.RequireConnection("LIRO_TEST_POSTGRES");
        suite.AsyncTest("Cinco conexiones reservan artículos distintos sin consultar dos veces el mismo", async () =>
        {
            var schema = "market_parallel_test_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
                await command.ExecuteNonQueryAsync();
            try
            {
                await using (var command = new NpgsqlCommand($"""
                    CREATE TABLE {schema}."Items" ("Id" uuid PRIMARY KEY,"CreatorId" bigint,"CreatorType" text,"MarketStatus" text,"CollectibleItemId" uuid);
                    CREATE TABLE {schema}."MarketWatches" ("ItemId" uuid PRIMARY KEY,"NextCheckAtUtc" timestamptz,"LeaseId" uuid,"LeaseUntilUtc" timestamptz,"LastAttemptAtUtc" timestamptz,"LastSuccessAtUtc" timestamptz,"LastErrorCode" text,"Activity" text DEFAULT 'SinDatos');
                    INSERT INTO {schema}."Items" SELECT gen_random_uuid(),1,'User','Limited',gen_random_uuid() FROM generate_series(1,5);
                    INSERT INTO {schema}."MarketWatches" ("ItemId","NextCheckAtUtc") SELECT "Id",NOW() FROM {schema}."Items";
                    """, admin))
                    await command.ExecuteNonQueryAsync();
                var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false };
                async Task<MarketClaim?> Claim()
                {
                    await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(builder.ConnectionString).Options);
                    return await new MarketStore(db).ClaimAsync(default);
                }
                var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Claim()));
                Check.That(claims.All(c => c is not null) && claims.Select(c => c!.ItemId).Distinct().Count() == 5, "Reservas simultáneas duplicadas o perdidas");
                Check.That(await Claim() is null, "Volvió a reservar un artículo en curso");
            }
            finally { await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await command.ExecuteNonQueryAsync(); }
        });
    }
}

