using Liro.MarketWorker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Liro.RegressionTests;

internal static class MarketLifecycleTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Mercado impide una segunda instancia y permite reiniciar tras liberar el bloqueo", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "liro-market-test-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "worker.lock");
            try
            {
                using (var first = MarketWorkerInstance.Acquire(path))
                    Check.Throws<IOException>(() => MarketWorkerInstance.Acquire(path));
                Check.That(File.Exists(path), "El caso de archivo residual no quedó representado");
                using var restarted = MarketWorkerInstance.Acquire(path);
            }
            finally { if (Directory.Exists(directory)) { File.Delete(path); Directory.Delete(directory); } }
        });
        suite.AsyncTest("Mercado cancela la espera de recuperación al detener el backend", async () =>
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            using var worker = new MarketWorkerService(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new MarketOptions { Enabled = true }), null!, NullLogger<MarketWorkerService>.Instance);
            await worker.StartAsync(default);
            // No hay DbContext: el trabajador entra en su espera de recuperación de 30 segundos.
            await Task.Delay(100);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            Check.That(worker.ExecuteTask!.IsCompleted, "El trabajador siguió activo después de Stop");
        });
    }
}
