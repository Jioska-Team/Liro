using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Liro.HistoryRecovery;

public sealed class InternetArchiveClient(InternetArchiveStore store) : IDisposable
{
    private readonly HttpClient http=new(new HttpClientHandler {UseProxy=false,UseCookies=false,AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All})
        {Timeout=TimeSpan.FromSeconds(45),MaxResponseContentBufferSize=8*1024*1024};
    public static string IndexUrl(string original,string? resume)
    {
        var url="https://web.archive.org/cdx/search/cdx?url="+Uri.EscapeDataString(original)+"&matchType=exact&output=json&filter=statuscode%3A200&collapse=digest&fl=timestamp,original,digest&limit=250&showResumeKey=true";
        return resume==null?url:url+"&resumeKey="+Uri.EscapeDataString(WebUtility.UrlDecode(resume));
    }
    public async Task<string> GetAsync(string url,CancellationToken ct)
    {
        var uri=new Uri(url);
        if(uri.Scheme!="https" || uri.Host!="web.archive.org" || !uri.IsDefaultPort || uri.UserInfo!="" || uri.Fragment!="")
            throw new ArgumentException("Solo se permite Internet Archive.");
        await store.EnsureCapacityAsync(ct);
        await store.ReserveRequestAsync(ct);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.UserAgent.ParseAdd("Liro-HistoryRecovery/1.1");
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
        {
            var wait=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow)??TimeSpan.FromMinutes(15);
            throw new SourceHttpException((int)response.StatusCode,wait>TimeSpan.FromSeconds(15)?wait:TimeSpan.FromSeconds(15));
        }
        return await response.Content.ReadAsStringAsync(ct);
    }
    public void Dispose()=>http.Dispose();
}

public sealed class InternetArchiveRunner(InternetArchiveStore store,string cacheDir)
{
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    public async Task<int> ImportAsync(int limit,CancellationToken ct)
    {
        var files=Directory.EnumerateFiles(cacheDir,"*.capture.json").OrderBy(x=>x).Take(limit);
        var added=0;
        foreach(var file in files)
        {
            if(new FileInfo(file).Length>8*1024*1024) throw new JsonException("Captura excesiva.");
            var raw=await File.ReadAllBytesAsync(file,ct);
            var capture=JsonSerializer.Deserialize<ArchiveCapture>(raw)??throw new JsonException();
            var job=await store.FindAsync(capture.RobloxId,capture.ItemType,ct)??throw new JsonException("Artículo no pendiente en la cola de archivo.");
            var points=capture.Validate(job.Routes);
            await store.EnqueueAsync(job,[new(capture.OriginalUrl,capture.CapturedAtUtc,"")],ct);
            var result=await store.SaveAsync(job,capture,Hash(raw),ct);added+=result.Added;
            Console.WriteLine($"Archivo {capture.ItemType} {capture.RobloxId}: {points.Count} puntos; {result.Added} nuevos; {result.Conflicts} diferencias conservadas sin sobrescribir.");
        }
        Console.WriteLine($"Importación local: {added} puntos nuevos; sin solicitudes externas.");
        await store.StatusAsync(ct);return 0;
    }
    public async Task<int> RunAsync(int limit,CancellationToken ct)
    {
        using var client=new InternetArchiveClient(store);
        var preferCapture=false;
        for(var n=0;n<limit;)
        {
            ct.ThrowIfCancellationRequested();
            if(!await store.EnabledAsync(ct))throw new ArchiveDisabledException();
            var capture=await store.NextCaptureAsync(ct);
            var indexJob=await store.NextJobAsync(ct);
            if(capture==null && indexJob==null)
            {
                var retry=await store.NextRetryAsync(ct);
                if(retry==null) break;
                await Task.Delay(retry>TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):retry.Value,ct);continue;
            }
            var processCapture=capture!=null&&(preferCapture||indexJob==null);
            var job=processCapture?capture!.Job:indexJob!;
            var replay=processCapture?capture!.Capture.ReplayUrl:null;
            try
            {
                if(processCapture)
                {
                    var entry=capture!.Capture;
                    var file=Path.Combine(cacheDir,Hash(Encoding.UTF8.GetBytes(entry.ReplayUrl))+".capture.json");
                    byte[] raw;
                    if(File.Exists(file))
                    {
                        if(new FileInfo(file).Length>8*1024*1024)throw new JsonException("Captura excesiva.");
                        raw=await File.ReadAllBytesAsync(file,ct);
                    }
                    else
                    {
                        var body=await client.GetAsync(entry.ReplayUrl,ct);
                        using var document=JsonDocument.Parse(body);
                        var envelope=new ArchiveCapture(job.RobloxId,job.ItemType,entry.OriginalUrl,entry.CapturedAtUtc,DateTime.UtcNow,document.RootElement.Clone());
                        envelope.Validate(job.Routes);
                        raw=JsonSerializer.SerializeToUtf8Bytes(envelope);
                        await File.WriteAllBytesAsync(file+".tmp",raw,ct);File.Move(file+".tmp",file,true);
                    }
                    var saved=JsonSerializer.Deserialize<ArchiveCapture>(raw)??throw new JsonException();
                    if(saved.RobloxId!=job.RobloxId || saved.ItemType!=job.ItemType || saved.ReplayUrl!=entry.ReplayUrl)throw new JsonException("Captura ajena.");
                    var result=await store.SaveAsync(job,saved,Hash(raw),ct);
                    Console.WriteLine($"{DateTime.UtcNow:O} Captura {job.RobloxId} {entry.CapturedAtUtc:yyyy-MM-dd}: {result.Added} puntos nuevos; {result.Conflicts} diferencias.");
                }
                else
                {
                    var routes=job.Routes;
                    if(job.RouteIndex>=routes.Count) {await store.AdvanceAsync(job,new([],null),ct);n++;continue;}
                    var url=routes[job.RouteIndex];
                    var body=await client.GetAsync(InternetArchiveClient.IndexUrl(url,job.ResumeKey),ct);
                    var page=InternetArchiveData.ParseIndex(body,url);
                    if(page.ResumeKey!=null && page.ResumeKey==job.ResumeKey) throw new JsonException("Cursor repetido.");
                    await store.AdvanceAsync(job,page,ct);
                    Console.WriteLine($"{DateTime.UtcNow:O} Índice {job.ItemType} {job.RobloxId}, ruta {job.RouteIndex+1}: {page.Rows.Count} capturas; continuación: {page.ResumeKey!=null}.");
                }
                n++;preferCapture=!processCapture;
            }
            catch(RecoveryBusyException)
            {
                Console.WriteLine("La base tiene trabajo prioritario; recuperación en espera 15 segundos.");await Task.Delay(TimeSpan.FromSeconds(15),ct);
            }
            catch(SourceHttpException e)
            {
                await store.ErrorAsync(job,replay,"HTTP"+e.Status,ct);
                if(e.Status is 403 or 429 or >=500)
                    await store.CooldownAsync(e.RetryAfter,"HTTP"+e.Status,e.Status==403,ct);
                Console.WriteLine($"Internet Archive HTTP {e.Status}; progreso conservado.");
                if(e.Status==403)return 5;
                n++;preferCapture=!processCapture;
            }
            catch(Exception e) when(e is JsonException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                await store.ErrorAsync(job,replay,e is JsonException?"CapturaNoValida":"ErrorConexion",ct);
                Console.WriteLine($"{DateTime.UtcNow:O} Archivo {job.RobloxId}: {e.GetType().Name}; se conserva el trabajo pendiente.");
                n++;preferCapture=!processCapture;
            }
        }
        await store.StatusAsync(ct);return 0;
    }
}
