using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Liro.MarketWorker;

public sealed class MarketWorkerService(IServiceScopeFactory scopes, IOptions<MarketOptions> settings,
    MarketTransport transport, ILogger<MarketWorkerService> logger) : BackgroundService
{
    private int selection;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            logger.LogInformation("Mercado desactivado: no se programan artículos ni se realizan solicitudes.");
            return;
        }
        var pool = new MarketLanePool(settings.Value.MaxConcurrentRequests);
        using var writes = new SemaphoreSlim(1, 1);
        var nextSeed = DateTime.MinValue;
        var nextConnections = DateTime.MinValue;
        var nextClassification = DateTime.MinValue;
        var connections = new List<(string Key, MarketProxy Proxy)>();
        var schemaReady = false;
        var cursor = 0;
        var schedule = settings.Value;
        bool? previousDirect = null;
        var directSchedule = settings.Value;
        logger.LogInformation("Mercado paralelo: máximo {Concurrency} consultas; presupuesto {Budget} por minuto; un guardado simultáneo.", settings.Value.MaxConcurrentRequests, settings.Value.RequestsPerMinute);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<LiroDbContext>();
                    if (!schemaReady && (await db.Database.GetPendingMigrationsAsync(ct)).Any())
                    {
                        logger.LogWarning("Mercado espera la migración de la base de datos.");
                        await Task.Delay(TimeSpan.FromSeconds(30), ct); continue;
                    }
                    schemaReady = true;
                    var store = new MarketStore(db);
                    if (DateTime.UtcNow >= nextClassification)
                    {
                        await writes.WaitAsync(ct);
                        try { logger.LogInformation("Clasificación desde historial guardado: {Count} artículos actualizados.",
                            await store.ReclassifyAsync(DateTime.UtcNow, ct)); }
                        finally { writes.Release(); }
                        nextClassification = DateTime.UtcNow.AddMinutes(5);
                        nextConnections = DateTime.MinValue;
                    }
                    if (DateTime.UtcNow >= nextConnections)
                    {
                        var updated = new List<(string Key, MarketProxy Proxy)>();
                        var unique = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var entry in await store.ConnectionsAsync(ct))
                        {
                            try
                            {
                                var proxy = MarketProxy.Parse(entry);
                                if (unique.Add(MarketProxy.RouteKey(proxy.Address))) updated.Add((entry.Clave, proxy));
                            }
                            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                            { logger.LogWarning("Configuración inválida en conexión de mercado {Id}; se omite.", entry.Id); }
                        }
                        if (updated.Count > 32) throw new InvalidOperationException("Demasiadas conexiones.");
                        connections = updated;
                        schedule = MarketSchedulePlan.Build(settings.Value, await store.PopulationAsync(ct),
                            connections.Sum(x => x.Proxy.RequestsPerMinute));
                        logger.LogInformation("Frecuencias de mercado: muy alta {Very} min; alta {High} min; media {Normal} min; baja {Low} min; sin ventas recientes o inactiva {Inactive} min; incierta reciente {Uncertain} min; sin historial {Missing} min; una selección de cada cuatro reservada al resto.",
                            schedule.VeryActiveMinutes, schedule.ActiveMinutes, schedule.NormalMinutes, schedule.LowMinutes, schedule.InactiveMinutes, schedule.UncertainMinutes, schedule.MissingHistoryMinutes);
                        nextConnections = DateTime.UtcNow.AddMinutes(1);
                        logger.LogInformation("Conexiones válidas de mercado: {Count}.", connections.Count);
                    }
                    if (DateTime.UtcNow >= nextSeed)
                    {
                        await writes.WaitAsync(ct);
                        try { logger.LogInformation("Seguimientos de mercado incorporados: {Count}.", await store.SeedAsync(ct)); }
                        finally { writes.Release(); }
                        nextSeed = DateTime.UtcNow.AddMinutes(5);
                    }
                    var proxies = connections.Select(x => x.Proxy).ToArray();
                    var useDirect = await transport.ShouldUseDirectAsync(proxies);
                    if (previousDirect != useDirect)
                    {
                        logger.LogInformation("Ruta de mercado: {Mode}; respaldo máximo {Budget}/min, subordinado al cupo del catálogo.",
                            useDirect ? "DIRECTA_RESPALDO" : "PROXIES", settings.Value.DirectRequestsPerMinute);
                        if (useDirect) directSchedule = MarketSchedulePlan.Build(settings.Value, await store.PopulationAsync(ct), settings.Value.DirectRequestsPerMinute);
                        previousDirect = useDirect;
                    }
                    if (useDirect)
                    {
                        pool.TryStart("DIRECTA_RESPALDO", () => ProcessAsync("DIRECTA_RESPALDO", null, writes, directSchedule, ct, proxies));
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        continue;
                    }
                    // Rotate the first offered lane so a larger pool cannot starve its last routes.
                    for (var i = 0; i < connections.Count; i++)
                    {
                        var route = connections[(cursor+i)%connections.Count];
                        pool.TryStart(MarketProxy.RouteKey(route.Proxy.Address), () => ProcessAsync(route.Key, route.Proxy, writes, schedule, ct));
                    }
                    if (connections.Count > 0) cursor = (cursor+1)%connections.Count;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    logger.LogWarning("Mercado no disponible; se reintentará sin afectar al catálogo.");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
            }
        }
        finally { await pool.DrainAsync(); }
    }

    private async Task ProcessAsync(string key, MarketProxy? proxy, SemaphoreSlim writes, MarketOptions schedule, CancellationToken ct,
        IReadOnlyList<MarketProxy>? proxies = null)
    {
        using var scope = scopes.CreateScope();
        var store = new MarketStore(scope.ServiceProvider.GetRequiredService<LiroDbContext>());
        MarketClaim? claim = null;
        try
        {
            var proxyCooldown = proxy is null ? TimeSpan.Zero : await transport.ProxyCooldownAsync(proxy);
            if (proxyCooldown > TimeSpan.Zero)
            {
                await DelayAsync(proxyCooldown < TimeSpan.FromSeconds(30) ? proxyCooldown : TimeSpan.FromSeconds(30), ct);
                return;
            }
            await writes.WaitAsync(ct);
            try { claim = await store.ClaimAsync(ct, selection % 4 == 3, schedule); if (claim is not null) selection = (selection + 1) % 4; }
            finally { writes.Release(); }
            if (claim is null) { await Task.Delay(TimeSpan.FromSeconds(10), ct); return; }
            ResaleObservation result;
            var retryUntil = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try
                {
                    result = proxy is null
                        ? await transport.ReadDirectAsync(claim.CollectibleItemId, proxies ?? [], ct)
                        : await transport.ReadAsync(proxy, claim.CollectibleItemId, ct);
                    break;
                }
                catch (MarketHttpException ex) when (ex.Code.StartsWith("Cupo", StringComparison.Ordinal)
                    && DateTime.UtcNow + ex.Delay < retryUntil)
                {
                    // Keep the item and its fair-selection slot; no HTTP request was sent.
                    await Task.Delay(ex.Delay > TimeSpan.Zero ? ex.Delay : TimeSpan.FromMilliseconds(100), ct);
                }
            }
            await writes.WaitAsync(ct);
            bool saved;
            try { saved = await store.SaveAsync(claim, result, schedule, ct); }
            finally { writes.Release(); }
            logger.LogInformation("Mercado: artículo {ItemId}; guardado {Saved}; puntos históricos {Count}; conexión {Connection}.", claim.ItemId, saved, result.Points.Count, key);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (MarketHttpException ex)
        {
            if (claim is not null)
            {
                var failure = !ex.Limited && !ex.Code.StartsWith("Cupo", StringComparison.Ordinal);
                await ReleaseAsync(store, claim, ex.Code, ex.Delay, failure, writes, ct);
                logger.LogWarning("Mercado: artículo {ItemId}; resultado {Code}; espera mínima {Seconds} segundos; conexión {Connection}.", claim.ItemId, ex.Code, ex.Delay.TotalSeconds, key);
            }
            await DelayAsync(ex.Delay < TimeSpan.FromSeconds(30) ? ex.Delay : TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex)
        {
            if (claim is not null) await ReleaseAsync(store, claim, "ErrorConsultaOGuardado", TimeSpan.FromMinutes(1), true, writes, ct);
            logger.LogWarning("Falló la consulta o guardado de mercado; tipo {Type}; HTTP {HttpStatus}; SQL {SqlState}; datos anteriores conservados.",
                ex.GetType().Name, (ex as HttpRequestException)?.StatusCode, (ex as Npgsql.PostgresException)?.SqlState);
            await DelayAsync(TimeSpan.FromSeconds(3), ct);
        }
        finally
        {
            // Cancellation leaves an expiring lease; no second worker can claim it while a request is still in flight.
            await DelayAsync(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task ReleaseAsync(MarketStore store, MarketClaim claim, string code, TimeSpan delay, bool failure, SemaphoreSlim writes, CancellationToken ct)
    {
        try
        {
            await writes.WaitAsync(ct);
            try { await store.ReleaseAsync(claim, code, delay, failure, ct); }
            finally { writes.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("No se pudo liberar una reserva de mercado; se conserva hasta su vencimiento."); }
    }
    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    { try { await Task.Delay(delay, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
}


