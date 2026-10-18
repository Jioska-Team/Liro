using System.Text.Json;
using System.Globalization;
namespace Liro.HistoryRecovery;
public sealed record ArchivePoint(string Metric, DateTime DayUtc, DateTime OriginalUtc, decimal Value);
public sealed record ArchiveCapture(long RobloxId,string ItemType,string OriginalUrl,DateTime CapturedAtUtc,DateTime FetchedAtUtc,JsonElement Payload)
{
    public string ReplayUrl => $"https://web.archive.org/web/{CapturedAtUtc:yyyyMMddHHmmss}id_/{OriginalUrl}";
    public IReadOnlyList<ArchivePoint> Validate(IReadOnlyList<string> allowedUrls)
    {
        if(RobloxId<=0 || ItemType is not ("Asset" or "Bundle") || CapturedAtUtc.Kind!=DateTimeKind.Utc || FetchedAtUtc.Kind!=DateTimeKind.Utc
            || CapturedAtUtc>FetchedAtUtc || FetchedAtUtc>DateTime.UtcNow.AddMinutes(5) || !InternetArchiveData.IsAllowedOriginal(OriginalUrl,allowedUrls))
            throw new JsonException("Identidad de captura inválida.");
        return InternetArchiveData.Parse(Payload.GetRawText(),CapturedAtUtc);
    }
}
public sealed record ArchiveIndexRow(string OriginalUrl,DateTime CapturedAtUtc,string Digest)
{
    public string ReplayUrl => $"https://web.archive.org/web/{CapturedAtUtc:yyyyMMddHHmmss}id_/{OriginalUrl}";
}
public sealed record ArchiveIndexPage(IReadOnlyList<ArchiveIndexRow> Rows,string? ResumeKey);
public static class InternetArchiveData
{
    public static IReadOnlyList<ArchivePoint> Parse(string json,DateTime capturedAt)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            var points=new Dictionary<(string,DateTime),ArchivePoint>();
            foreach(var (field,metric) in new[]{("priceDataPoints","PrecioMedioVenta"),("volumeDataPoints","VolumenVentas")})
            {
                var series=doc.RootElement.GetProperty(field);
                if(series.GetArrayLength()>10000) throw new JsonException("Serie excesiva.");
                foreach(var row in series.EnumerateArray())
                {
                    var date=row.GetProperty("date").GetDateTimeOffset().UtcDateTime;
                    var value=row.GetProperty("value").GetDecimal();
                    if(date.Year<2006 || date>capturedAt.AddMinutes(5) || value is <0 or >1_000_000_000_000m
                        || (metric=="VolumenVentas" && decimal.Truncate(value)!=value)) throw new JsonException("Punto inválido.");
                    var point=new ArchivePoint(metric,date.Date,date,value);
                    if(points.TryGetValue((metric,date.Date),out var old))
                    {
                        if(old.Value!=value) throw new JsonException("Valores contradictorios en un día.");
                    }
                    else points.Add((metric,date.Date),point);
                }
            }
            return points.Values.OrderBy(p=>p.DayUtc).ThenBy(p=>p.Metric).ToArray();
        }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new JsonException("Formato histórico inválido.",e); }
    }
    public static bool IsAllowedOriginal(string original,IReadOnlyList<string> allowed)
    {
        if(!Uri.TryCreate(original,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https")
            || !uri.IsDefaultPort || uri.UserInfo!="" || uri.Fragment!="") return false;
        return allowed.Any(a=>{var expected=new Uri(a);return uri.Host==expected.Host && uri.PathAndQuery.Equals(expected.PathAndQuery,StringComparison.OrdinalIgnoreCase);});
    }
    public static ArchiveIndexPage ParseIndex(string json,string requestedUrl)
    {
        using var doc=JsonDocument.Parse(json);
        var rows=doc.RootElement;
        if(rows.ValueKind!=JsonValueKind.Array) throw new JsonException("Índice inválido.");
        if(rows.GetArrayLength()==0) return new([],null);
        var headers=rows[0].EnumerateArray().Select(x=>x.GetString()).ToArray();
        var ti=Array.IndexOf(headers,"timestamp");var oi=Array.IndexOf(headers,"original");var di=Array.IndexOf(headers,"digest");
        if(ti<0 || oi<0 || di<0) throw new JsonException("Faltan columnas CDX.");
        var result=new List<ArchiveIndexRow>();string? resume=null;var footer=false;
        foreach(var row in rows.EnumerateArray().Skip(1))
        {
            if(row.GetArrayLength()==0) {footer=true;continue;}
            if(footer)
            {
                if(row.GetArrayLength()!=1 || resume!=null) throw new JsonException("Cursor inválido.");
                resume=row[0].GetString();if(string.IsNullOrWhiteSpace(resume) || resume.Length>8000) throw new JsonException("Cursor inválido.");continue;
            }
            if(row.GetArrayLength()!=headers.Length) throw new JsonException("Fila CDX incompleta.");
            var original=row[oi].GetString()!;
            if(!IsAllowedOriginal(original,[requestedUrl]) || !DateTime.TryParseExact(row[ti].GetString(),"yyyyMMddHHmmss",CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var stamp) || stamp>DateTime.UtcNow) throw new JsonException("Captura ajena a la consulta.");
            result.Add(new(original,stamp,row[di].GetString()!));
        }
        if(footer && resume==null) throw new JsonException("Cursor incompleto.");
        return new(result,resume);
    }
    public static IReadOnlyList<string> Routes(string type,long id,Guid? collectibleId)
    {
        var routes=new List<string>();
        if(type=="Asset") {routes.Add($"https://economy.roblox.com/v1/assets/{id}/resale-data");routes.Add($"https://www.roblox.com/asset-sales-data?assetId={id}");}
        if(collectibleId is {} guid) routes.Add($"https://apis.roblox.com/marketplace-sales/v1/item/{guid:D}/resale-data");
        return routes;
    }
}
