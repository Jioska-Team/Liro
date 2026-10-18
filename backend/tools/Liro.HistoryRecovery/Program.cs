using System.Security.Cryptography;
using System.Text.Json;
using Liro.HistoryRecovery;
using Npgsql;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_,e) => { e.Cancel=true; cancel.Cancel(); };
try
{
    if(args.Length==0 || args[0] is not ("init" or "status" or "run" or "archive-import" or "archive-run" or "archive-status" or "archive-enable" or "archive-stop"))
    { Console.WriteLine("Uso: init | status | run | archive-import | archive-run | archive-status | archive-enable | archive-stop; --cache RUTA --limit N --download."); return 2; }
    var cacheDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Liro","HistoryRecovery");
    var limit=10; var download=false;
    for(var i=1;i<args.Length;i++)
    {
        switch(args[i])
        {
            case "--cache" when i+1<args.Length: cacheDir=Path.GetFullPath(args[++i]); break;
            case "--limit" when i+1<args.Length: limit=int.Parse(args[++i]); if(limit is <1 or >1000000) throw new ArgumentException(); break;
            case "--download": download=true;break;
            default: throw new ArgumentException("Parámetro desconocido.");
        }
    }
    var connection=Environment.GetEnvironmentVariable("LIRO_HISTORY_CONNECTION") ?? throw new ArgumentException("Falta LIRO_HISTORY_CONNECTION.");
    var builder=new NpgsqlConnectionStringBuilder(connection) { ApplicationName="Liro.HistoryRecovery",MaxPoolSize=1,CommandTimeout=5,Timeout=5 };
    await using var db=new NpgsqlConnection(builder.ConnectionString);
    await db.OpenAsync(cancel.Token);
    // Keep all administrative statements bounded too.
    await using(var limits=new NpgsqlCommand("SET lock_timeout='250ms'; SET statement_timeout='5s';",db)) await limits.ExecuteNonQueryAsync(cancel.Token);
    var store=new RecoveryStore(db);
    var archive=new InternetArchiveStore(db);
    if(args[0]=="archive-status") {await archive.StatusAsync(cancel.Token);return 0;}
    if(args[0] is "archive-enable" or "archive-stop") {await archive.EnableAsync(args[0]=="archive-enable",cancel.Token);Console.WriteLine(args[0]=="archive-enable"?"Búsqueda en Internet Archive habilitada.":"Búsqueda en Internet Archive desactivada.");return 0;}
    if(args[0]=="status") { await store.StatusAsync(cancel.Token);return 0; }
    if(!await store.AcquireAsync(cancel.Token)) { Console.WriteLine("Ya hay una recuperación activa; no se inicia otra.");return 3; }
    if(args[0]=="init") { await store.InitializeAsync(cancel.Token);await store.SeedAsync(cancel.Token);await archive.SeedAsync(cancel.Token);await store.StatusAsync(cancel.Token);return 0; }
    await store.SeedAsync(cancel.Token);
    Directory.CreateDirectory(cacheDir);
    if(args[0] is "archive-import" or "archive-run")
    {
        await archive.SeedAsync(cancel.Token);
        var runner=new InternetArchiveRunner(archive,cacheDir);
        if(args[0]=="archive-import")return await runner.ImportAsync(limit,cancel.Token);
        if(!download) {Console.WriteLine("La búsqueda remota requiere --download.");return 2;}
        return await runner.RunAsync(limit,cancel.Token);
    }
    using var downloader=new HistoryDownloader();
    var processed=0;var inserted=0;var skipped=0;
    foreach(var job in await store.JobsAsync(cancel.Token))
    {
        if(processed>=limit) break;
        cancel.Token.ThrowIfCancellationRequested();
        var file=Path.Combine(cacheDir,$"{job.ItemType}-{job.RobloxId}.json");
        try
        {
            if(!File.Exists(file))
            {
                if(!download) { skipped++;continue; }
                while(true)
                {
                    var state=await store.DownloadStateAsync(cancel.Token);
                    if(!state.Allowed) { Console.WriteLine("Descargas desactivadas: falta habilitación y referencia del permiso de la fuente."); return 4; }
                    if(state.Delay>TimeSpan.Zero) await Task.Delay(state.Delay>TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):state.Delay,cancel.Token);
                    if(await store.ReserveDownloadAsync(cancel.Token) is not null) break;
                }
                var fetched=await downloader.DownloadAsync(job.ItemType,job.RobloxId,cancel.Token);
                // Atomic cache publication: interrupted writes never become valid input.
                await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(fetched),cancel.Token);
                File.Move(file+".tmp",file,true);
            }
            if(new FileInfo(file).Length>8*1024*1024) throw new JsonException("Archivo excesivo.");
            var raw=await File.ReadAllBytesAsync(file,cancel.Token);
            var cached=JsonSerializer.Deserialize<CachedHistory>(raw) ?? throw new JsonException();
            var days=cached.Validate(job.ItemType,job.RobloxId);
            var conflicts=await store.ConflictsAsync(job.ItemId,days,cancel.Token);
            var added=conflicts==0?await store.SaveAsync(job.ItemId,days,cached.FetchedAtUtc,cancel.Token):0;
            await store.CompleteAsync(job.ItemId,cached,days,Convert.ToHexString(SHA256.HashData(raw)),conflicts,cancel.Token);
            inserted+=added; processed++;
            Console.WriteLine($"{job.ItemType} {job.RobloxId}: {days.Count} fechas; {added} puntos nuevos; {conflicts} discrepancias.");
        }
        catch(SourceHttpException e)
        {
            await store.FailureAsync(job.ItemId,"HTTP"+e.Status,cancel.Token);
            if(e.Status is 403 or 429) { await store.CooldownAsync(e.RetryAfter,cancel.Token);Console.WriteLine($"HTTP {e.Status}: recuperación detenida; se conserva el progreso y la espera indicada.");return 5; }
            processed++;
        }
        catch(JsonException) { await store.FailureAsync(job.ItemId,"DatosInvalidos",cancel.Token);processed++;Console.WriteLine($"{job.ItemType} {job.RobloxId}: datos inválidos; no se importan."); }
        catch(HttpRequestException) { await store.FailureAsync(job.ItemId,"ErrorConexion",cancel.Token);Console.WriteLine("Error de conexión; se conserva el progreso.");return 5; }
    }
    Console.WriteLine($"Finalizado: {processed} artículos procesados, {inserted} puntos nuevos, {skipped} sin archivo local. No se consultó Roblox.");
    await store.StatusAsync(cancel.Token);
    return 0;
}
catch(OperationCanceledException) { Console.WriteLine("Recuperación detenida; se conserva el progreso confirmado.");return 6; }
catch(RecoveryBusyException) { Console.WriteLine("La base tiene trabajo prioritario; recuperación detenida. Se conserva el progreso para reanudar.");return 8; }
catch(ArchiveDisabledException) {Console.WriteLine("La búsqueda en Internet Archive está desactivada; progreso conservado.");return 4;}
catch(PostgresException e) { Console.WriteLine($"Base de datos: {e.SqlState}. Recuperación detenida para no competir con los procesos actuales.");return 7; }
catch(Exception e) { Console.WriteLine($"No se completó la recuperación ({e.GetType().Name}). No se muestran credenciales.");return 1; }
