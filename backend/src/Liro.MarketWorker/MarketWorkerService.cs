using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Liro.MarketWorker;

public sealed class MarketWorkerService(IServiceScopeFactory scopes, IOptions<MarketOptions> settings,
    MarketTransport transport, ILogger<MarketWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            logger.LogInformation("Mercado desactivado: no se programan artículos ni se realizan solicitudes.");
            return;
        }
        var nextSeed = DateTime.MinValue;
        var nextConnections = DateTime.MinValue;
        var connections = new List<(string Key, MarketProxy Proxy)>();
        var routeIndex = 0;
        var schemaReady = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LiroDbContext>();
                // This process never applies migrations or performs catalog startup housekeeping.
                if (!schemaReady && (await db.Database.GetPendingMigrationsAsync(ct)).Any())
                {
                    logger.LogWarning("Mercado espera la migración de la base de datos.");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct); continue;
                }
                schemaReady = true;
                var store = new MarketStore(db);
                if (DateTime.UtcNow >= nextConnections)
                {
                    connections.Clear();
                    var entries = await store.ConnectionsAsync(ct);
                    if (entries.Count > 32) throw new InvalidOperationException("Demasiadas conexiones.");
                    foreach (var entry in entries)
                    {
                        try { connections.Add((entry.Clave, MarketProxy.Parse(entry))); }
                        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                        { logger.LogWarning("Configuración inválida en conexión de mercado {Id}; se omite.", entry.Id); }
                    }
                    nextConnections = DateTime.UtcNow.AddMinutes(1);
                    logger.LogInformation("Conexiones válidas de mercado: {Count}.", connections.Count);
                }
                if (connections.Count == 0) { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                if (DateTime.UtcNow >= nextSeed)
                {
                    var added = await store.SeedAsync(ct);
                    nextSeed = DateTime.UtcNow.AddMinutes(5);
                    logger.LogInformation("Seguimientos de mercado incorporados: {Count}.", added);
                }
                var claim = await store.ClaimAsync(ct);
                if (claim is null) { await Task.Delay(TimeSpan.FromSeconds(10), ct); continue; }
                routeIndex %= connections.Count;
                var route = connections[routeIndex];
                routeIndex = (routeIndex + 1) % connections.Count;
                try
                {
                    var result = await transport.ReadAsync(route.Proxy, claim.CollectibleItemId, ct);
                    var saved = await store.SaveAsync(claim, result, settings.Value, ct);
                    logger.LogInformation("Mercado: artículo {ItemId}; guardado {Saved}; puntos históricos {Count}.", claim.ItemId, saved, result.Points.Count);
                }
                catch (MarketHttpException ex)
                {
                    var failure = !ex.Limited && !ex.Code.StartsWith("Cupo", StringComparison.Ordinal);
                    await store.ReleaseAsync(claim, ex.Code, ex.Delay, failure, ct);
                    logger.LogWarning("Mercado: artículo {ItemId}; resultado {Code}; espera mínima {Seconds} segundos.", claim.ItemId, ex.Code, ex.Delay.TotalSeconds);
                    await Task.Delay(ex.Delay < TimeSpan.FromSeconds(30) ? ex.Delay : TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    await store.ReleaseAsync(claim, "ErrorConsultaOGuardado", TimeSpan.FromMinutes(1), true, ct);
                    logger.LogWarning("Falló la consulta o guardado de mercado para {ItemId}; datos anteriores conservados.", claim.ItemId);
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                logger.LogWarning("Mercado no disponible; se reintentará sin afectar al catálogo.");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }
}
