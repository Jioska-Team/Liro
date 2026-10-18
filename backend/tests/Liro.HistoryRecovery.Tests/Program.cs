using Liro.HistoryRecovery;
using System.Text.Json;

var failures = 0;
void Test(string name, Action test) { try { test(); Console.WriteLine("Correcto: " + name); } catch(Exception e) { failures++; Console.WriteLine("Falló: " + name + " (" + e.GetType().Name + ")"); } }
void Reject(string value) { try { HistorySeries.Parse(value, DateTime.UtcNow); } catch (JsonException) { return; } throw new Exception("Se aceptó una serie inválida"); }
Test("Conserva fecha UTC, precio y volumen", () => {
    var rows = HistorySeries.Parse("""{"num_points":1,"timestamp":[1472947200],"avg_daily_sales_price":[7539],"sales_volume":[60]}""", DateTime.UtcNow);
    if(rows.Count != 1 || rows[0] != new HistoryDay(new DateTime(2016,9,4,0,0,0,DateTimeKind.Utc),7539,60)) throw new Exception();
});
Test("Rechaza columnas desalineadas", () => Reject("""{"num_points":2,"timestamp":[1472947200],"avg_daily_sales_price":[7539,12],"sales_volume":[60,1]}"""));
Test("Rechaza fechas repetidas", () => Reject("""{"num_points":2,"timestamp":[1472947200,1472947200],"avg_daily_sales_price":[7539,12],"sales_volume":[60,1]}"""));
Test("Rechaza volumen fraccionario", () => Reject("""{"num_points":1,"timestamp":[1472947200],"avg_daily_sales_price":[7539],"sales_volume":[0.5]}"""));
Test("Rechaza valores negativos", () => Reject("""{"num_points":1,"timestamp":[1472947200],"avg_daily_sales_price":[-1],"sales_volume":[60]}"""));
Test("Rechaza fechas futuras", () => Reject("""{"num_points":1,"timestamp":[4102444800],"avg_daily_sales_price":[1],"sales_volume":[1]}"""));
Test("No inventa días para una serie vacía", () => { if(HistorySeries.Parse("""{"num_points":0,"timestamp":[],"avg_daily_sales_price":[],"sales_volume":[]}""",DateTime.UtcNow).Count != 0) throw new Exception(); });
Test("Rechaza archivos de otro artículo", () => {
    using var json=JsonDocument.Parse("""{"num_points":0,"timestamp":[],"avg_daily_sales_price":[],"sales_volume":[]}""");
    var cache=new CachedHistory("Asset",12,CachedHistory.Url("Asset",12),DateTime.UtcNow,json.RootElement.Clone());
    try { cache.Validate("Bundle",12); } catch(JsonException) { return; } throw new Exception();
});
Test("Una página de error no se interpreta como historial vacío", () => {
    try { CachedHistory.FromHtml("<html>Unavailable</html>","Asset",12,DateTime.UtcNow); } catch(JsonException) { return; } throw new Exception();
});
Test("Distingue la identidad del paquete", () => {
    var cache=CachedHistory.FromHtml("""var bundle_id = 12; var sales_data = {"num_points":0,"timestamp":[],"avg_daily_sales_price":[],"sales_volume":[]};""","Bundle",12,DateTime.UtcNow);
    if(cache.SourceUrl!="https://www.rolimons.com/bundle/12") throw new Exception();
});
Test("Archivo cruza métricas independientes sin rellenar ausentes", () => {
    var points=InternetArchiveData.Parse("""{"priceDataPoints":[{"date":"2019-03-06T06:00:00Z","value":500},{"date":"2019-03-07T06:00:00Z","value":600}],"volumeDataPoints":[{"date":"2019-03-07T06:00:00Z","value":2}]}""",new(2019,9,2,0,0,0,DateTimeKind.Utc));
    if(points.Count!=3 || points.Count(p=>p.Metric=="VolumenVentas")!=1 || points.Any(p=>p.DayUtc.Hour!=0) || points[0].OriginalUtc.Hour!=6) throw new Exception();
});
Test("Archivo rechaza dos valores contradictorios del mismo día", () => {
    try { InternetArchiveData.Parse("""{"priceDataPoints":[{"date":"2019-03-06T06:00:00Z","value":500},{"date":"2019-03-06T05:00:00Z","value":600}],"volumeDataPoints":[]}""",DateTime.UtcNow); }
    catch(JsonException) { return; } throw new Exception();
});
Test("Archivo no acepta fecha posterior a la captura", () => {
    try { InternetArchiveData.Parse("""{"priceDataPoints":[{"date":"2026-03-06T06:00:00Z","value":500}],"volumeDataPoints":[]}""",new(2019,9,2,0,0,0,DateTimeKind.Utc)); }
    catch(JsonException) { return; } throw new Exception();
});
Test("CDX conserva el cursor y no limita las capturas por año", () => {
    var page=InternetArchiveData.ParseIndex("""[["timestamp","original","digest"],["20190902161727","https://economy.roblox.com/v1/assets/151789690/resale-data","abc"],[],["com%2Croblox%29%2F+2019%21"]]""","https://economy.roblox.com/v1/assets/151789690/resale-data");
    if(page.Rows.Count!=1 || page.ResumeKey==null || page.Rows[0].CapturedAtUtc.Kind!=DateTimeKind.Utc)throw new Exception();
    var url=InternetArchiveClient.IndexUrl(page.Rows[0].OriginalUrl,page.ResumeKey);
    if(!url.Contains("showResumeKey=true") || url.Contains("collapse=timestamp") || url.Contains("%252C"))throw new Exception();
});
Test("CDX rechaza capturas de otro artículo", () => {
    try {InternetArchiveData.ParseIndex("""[["timestamp","original","digest"],["20190902161727","https://economy.roblox.com/v1/assets/999/resale-data","abc"]]""","https://economy.roblox.com/v1/assets/151789690/resale-data");}
    catch(JsonException){return;}throw new Exception();
});
Test("Los paquetes consultan su identificador coleccionable y no un ID de artículo ajeno", () => {
    var guid=Guid.Parse("00000000-0000-0000-0000-000000000001");var routes=InternetArchiveData.Routes("Bundle",12,guid);
    if(routes.Count!=1 || !routes[0].Contains(guid.ToString()) || routes.Any(x=>x.Contains("/assets/")))throw new Exception();
});
if(args.Contains("--database")) {
    try { await DatabaseChecks.RunAsync(); Console.WriteLine("Correcto: repetición, concurrencia, prioridad posterior de Roblox, reanudación y detención por bloqueo."); }
    catch(Exception e) { failures++;Console.WriteLine("Falló: integración aislada ("+e.GetType().Name+"): "+e.Message); }
}
if(args.Contains("--scheduling-database")) {
 try {await SchedulingChecks.RunAsync();Console.WriteLine("Correcto: prioridad, reparto persistente, capturas vacías, reintentos, ausencia de escrituras repetidas y métricas faltantes.");}
 catch(Exception e){failures++;Console.WriteLine("Falló el reparto: "+e.Message);}
}
return failures == 0 ? 0 : 1;

