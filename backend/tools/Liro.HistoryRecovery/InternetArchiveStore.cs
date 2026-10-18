using Npgsql;
namespace Liro.HistoryRecovery;

public sealed record ArchiveJob(Guid ItemId,long RobloxId,string ItemType,Guid? CollectibleId,int RouteIndex,string? ResumeKey)
{
    public IReadOnlyList<string> Routes => InternetArchiveData.Routes(ItemType,RobloxId,CollectibleId);
}
public sealed record PendingArchiveCapture(ArchiveJob Job,ArchiveIndexRow Capture);
public sealed class InternetArchiveStore(NpgsqlConnection db)
{
    private NpgsqlCommand Cmd(string sql, params (string Name,object Value)[] values)
    {
        var c=new NpgsqlCommand(sql,db);
        foreach(var (name,value) in values) c.Parameters.AddWithValue(name,value);
        return c;
    }
    public async Task SeedAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            INSERT INTO "InternetArchiveJobs" ("ItemId") SELECT j."ItemId" FROM "HistoryRecoveryJobs" j
            JOIN "Items" i ON i."Id"=j."ItemId"
            WHERE j."Status"='Pendiente' AND i."CreatorId"=1 AND i."CreatorType"='User'
            AND i."MarketStatus" IN ('Limited','LimitedUnique') ON CONFLICT DO NOTHING
            """);await c.ExecuteNonQueryAsync(ct);
    }
    public async Task EnableAsync(bool enable,CancellationToken ct)
    {
        await using var c=Cmd("UPDATE \"InternetArchiveControl\" SET \"Enabled\"=@v WHERE \"Id\"",("v",enable));await c.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> EnabledAsync(CancellationToken ct)
    {
        await using var c=Cmd("SELECT \"Enabled\" FROM \"InternetArchiveControl\" WHERE \"Id\"");return (bool)(await c.ExecuteScalarAsync(ct))!;
    }
    public async Task ReserveRequestAsync(CancellationToken ct)
    {
        while(true)
        {
            await using var c=Cmd("""
                SELECT "Enabled",GREATEST(INTERVAL '0 seconds',"NextRequestAtUtc"-NOW()) FROM "InternetArchiveControl" WHERE "Id"
                """);
            bool enabled;TimeSpan delay;
            await using(var r=await c.ExecuteReaderAsync(ct)) {await r.ReadAsync(ct);enabled=r.GetBoolean(0);delay=r.GetTimeSpan(1);}
            if(!enabled) throw new ArchiveDisabledException();
            if(delay>TimeSpan.Zero) {await Task.Delay(delay>TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):delay,ct);continue;}
            await using var reserve=Cmd("""
                UPDATE "InternetArchiveControl" SET "NextRequestAtUtc"=NOW()+INTERVAL '15 seconds',"LastRequestAtUtc"=NOW()
                WHERE "Id" AND "Enabled" AND "NextRequestAtUtc"<=NOW()
                """);if(await reserve.ExecuteNonQueryAsync(ct)==1) return;
        }
    }
    public async Task CooldownAsync(TimeSpan delay,string error,bool disable,CancellationToken ct)
    {
        await using var c=Cmd("""
            UPDATE "InternetArchiveControl" SET "NextRequestAtUtc"=GREATEST("NextRequestAtUtc",NOW()+@delay),
            "LastError"=@error,"Enabled"="Enabled" AND NOT @disable WHERE "Id"
            """,("delay",delay),("error",error),("disable",disable));await c.ExecuteNonQueryAsync(ct);
    }
    private static ArchiveJob ReadJob(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetInt64(1),r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetString(5));
    public async Task<ArchiveJob?> FindAsync(long id,string type,CancellationToken ct)
    {
        await using var c=Cmd("""
            SELECT i."Id",i."RobloxAssetId",i."ItemType",i."CollectibleItemId",j."RouteIndex",j."ResumeKey"
            FROM "InternetArchiveJobs" j JOIN "Items" i ON i."Id"=j."ItemId"
            WHERE i."RobloxAssetId"=@id AND i."ItemType"=@type
            """,("id",id),("type",type));await using var r=await c.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadJob(r):null;
    }
    public async Task<ArchiveJob?> NextJobAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            SELECT i."Id",i."RobloxAssetId",i."ItemType",i."CollectibleItemId",j."RouteIndex",j."ResumeKey"
            FROM "InternetArchiveJobs" j JOIN "Items" i ON i."Id"=j."ItemId"
            WHERE NOT j."IndexCompleted" AND j."Attempts"<3 AND j."NextAttemptAtUtc"<=NOW()
            ORDER BY j."RouteIndex",
                (EXISTS(SELECT 1 FROM "MarketHistoryPoints" p WHERE p."ItemId"=j."ItemId" AND p."Metric" IN ('PrecioMedioVenta','VolumenVentas'))
                 OR EXISTS(SELECT 1 FROM "MarketHistoryArchivePoints" p WHERE p."ItemId"=j."ItemId")
                 OR EXISTS(SELECT 1 FROM "MarketHistoryInternetArchivePoints" p WHERE p."ItemId"=j."ItemId")),
                j."LastAttemptAtUtc" NULLS FIRST,j."ItemId" LIMIT 1
            """);await using var r=await c.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadJob(r):null;
    }
    public async Task<PendingArchiveCapture?> NextCaptureAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            WITH turns AS (
                SELECT "ItemId",SUM("Attempts")+COUNT(*) FILTER (WHERE "ProcessedAtUtc" IS NOT NULL) AS used,
                    MAX("ProcessedAtUtc") AS last_processed
                FROM "InternetArchiveCaptures" GROUP BY "ItemId"
            ), due AS (
                SELECT DISTINCT ON ("ItemId") * FROM "InternetArchiveCaptures"
                WHERE "Status" IN ('Pendiente','Error') AND "Attempts"<3 AND "NextAttemptAtUtc"<=NOW()
                ORDER BY "ItemId","NextAttemptAtUtc","CapturedAtUtc","ReplayUrl"
            )
            SELECT i."Id",i."RobloxAssetId",i."ItemType",i."CollectibleItemId",j."RouteIndex",j."ResumeKey",
                c."OriginalUrl",c."CapturedAtUtc",COALESCE(c."Digest",'')
            FROM due c JOIN "Items" i ON i."Id"=c."ItemId" JOIN "InternetArchiveJobs" j ON j."ItemId"=c."ItemId"
            JOIN turns t ON t."ItemId"=c."ItemId"
            ORDER BY t.used,
                (EXISTS(SELECT 1 FROM "MarketHistoryPoints" p WHERE p."ItemId"=c."ItemId" AND p."Metric" IN ('PrecioMedioVenta','VolumenVentas'))
                 OR EXISTS(SELECT 1 FROM "MarketHistoryArchivePoints" p WHERE p."ItemId"=c."ItemId")
                 OR EXISTS(SELECT 1 FROM "MarketHistoryInternetArchivePoints" p WHERE p."ItemId"=c."ItemId")),
                t.last_processed NULLS FIRST,c."CapturedAtUtc",c."ItemId",c."ReplayUrl" LIMIT 1
            """);await using var r=await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(ReadJob(r),new(r.GetString(6),r.GetDateTime(7),r.GetString(8))):null;
    }
    public async Task EnqueueAsync(ArchiveJob job,IReadOnlyList<ArchiveIndexRow> rows,CancellationToken ct)
    {
        foreach(var batch in rows.Chunk(120))
        {
            await using var c=Cmd("""
                INSERT INTO "InternetArchiveCaptures" ("ItemId","ReplayUrl","OriginalUrl","CapturedAtUtc","Digest")
                SELECT @id,p.url,p.original,p.stamp,p.digest FROM unnest(@urls::text[],@originals::text[],@stamps::timestamptz[],@digests::text[]) p(url,original,stamp,digest)
                ON CONFLICT DO NOTHING
                """,("id",job.ItemId),("urls",batch.Select(x=>x.ReplayUrl).ToArray()),("originals",batch.Select(x=>x.OriginalUrl).ToArray()),
                ("stamps",batch.Select(x=>x.CapturedAtUtc).ToArray()),("digests",batch.Select(x=>x.Digest).ToArray()));await c.ExecuteNonQueryAsync(ct);
        }
    }
    public async Task AdvanceAsync(ArchiveJob job,ArchiveIndexPage page,CancellationToken ct)
    {
        // Insert first, then checkpoint. A crash between them repeats only an idempotent insert.
        await EnqueueAsync(job,page.Rows,ct);
        var next=page.ResumeKey==null?job.RouteIndex+1:job.RouteIndex;
        var done=next>=job.Routes.Count;
        await using var c=Cmd("""
            UPDATE "InternetArchiveJobs" SET "RouteIndex"=@route,"ResumeKey"=NULLIF(@resume,''),"IndexCompleted"=@done,
                "Status"=@status,"Attempts"=0,"LastError"=NULL,"LastAttemptAtUtc"=NOW()
            WHERE "ItemId"=@id
            """,("id",job.ItemId),("route",next),("resume",page.ResumeKey??""),("done",done),("status",done?"IndiceRecorrido":"Buscando"));await c.ExecuteNonQueryAsync(ct);
    }
    public async Task EnsureCapacityAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            SELECT COUNT(*) FILTER(WHERE state='active')>=4 OR COUNT(*) FILTER(WHERE wait_event_type='Lock')>0
            FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid()
            """);if((bool)(await c.ExecuteScalarAsync(ct))!) throw new RecoveryBusyException();
    }
    public async Task<(int Added,int Conflicts)> SaveAsync(ArchiveJob job,ArchiveCapture capture,string hash,CancellationToken ct)
    {
        var points=capture.Validate(job.Routes);var added=0;var conflicts=0;
        await EnsureCapacityAsync(ct);
        var existing=new Dictionary<(string Metric,DateTime Day),decimal>();
        if(points.Count>0)
        {
            await using var read=Cmd("""
                SELECT "Metric","PeriodUtc","Value" FROM "MarketHistoryCombined"
                WHERE "ItemId"=@id AND "PeriodUtc">=@first AND "PeriodUtc"<=@last
                """,("id",job.ItemId),("first",points.Min(p=>p.DayUtc)),("last",points.Max(p=>p.DayUtc)));
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))existing[(r.GetString(0),r.GetDateTime(1))]=r.GetDecimal(2);
        }
        var missing=new List<ArchivePoint>();
        foreach(var point in points)
        {
            if(existing.TryGetValue((point.Metric,point.DayUtc),out var value))
            {
                if(value!=point.Value)conflicts++;
            }
            else missing.Add(point);
        }
        // Keep the SQL existence check and unique key for data arriving after this
        // snapshot. A fully overlapping capture needs no INSERT or batch transaction.
        foreach(var batch in missing.Chunk(120))
        {
            await EnsureCapacityAsync(ct);
            await using var tx=await db.BeginTransactionAsync(ct);
            await using(var timeout=new NpgsqlCommand("SET LOCAL lock_timeout='250ms';SET LOCAL statement_timeout='3s';",db,tx)) await timeout.ExecuteNonQueryAsync(ct);
            await using var c=new NpgsqlCommand("""
                WITH p AS (SELECT * FROM unnest(@metrics::text[],@days::timestamptz[],@originals::timestamptz[],@values::numeric[]) t(metric,day,original,value)),
                conflicts AS (SELECT COUNT(*)::int n FROM p JOIN "MarketHistoryCombined" h
                    ON h."ItemId"=@id AND h."Metric"=p.metric AND h."PeriodUtc"=p.day WHERE h."Value"<>p.value),
                saved AS (
                    INSERT INTO "MarketHistoryInternetArchivePoints"
                    ("ItemId","Metric","PeriodUtc","OriginalPeriodUtc","Value","ObservedAtUtc","CapturedAtUtc","ReplayUrl","Sha256")
                    SELECT @id,p.metric,p.day,p.original,p.value,@observed,@captured,@url,@hash FROM p
                    WHERE NOT EXISTS (SELECT 1 FROM "MarketHistoryCombined" h WHERE h."ItemId"=@id AND h."Metric"=p.metric AND h."PeriodUtc"=p.day)
                    ON CONFLICT DO NOTHING RETURNING 1
                ) SELECT (SELECT COUNT(*)::int FROM saved),(SELECT n FROM conflicts)
                """,db,tx);
            c.Parameters.AddWithValue("id",job.ItemId);c.Parameters.AddWithValue("metrics",batch.Select(x=>x.Metric).ToArray());
            c.Parameters.AddWithValue("days",batch.Select(x=>x.DayUtc).ToArray());c.Parameters.AddWithValue("originals",batch.Select(x=>x.OriginalUtc).ToArray());
            c.Parameters.AddWithValue("values",batch.Select(x=>x.Value).ToArray());c.Parameters.AddWithValue("observed",capture.FetchedAtUtc);
            c.Parameters.AddWithValue("captured",capture.CapturedAtUtc);c.Parameters.AddWithValue("url",capture.ReplayUrl);c.Parameters.AddWithValue("hash",hash);
            await using(var r=await c.ExecuteReaderAsync(ct)) {await r.ReadAsync(ct);added+=r.GetInt32(0);conflicts+=r.GetInt32(1);}
            await tx.CommitAsync(ct);await Task.Delay(1000,ct);
        }
        await EnsureCapacityAsync(ct);
        await using var complete=Cmd("""
            UPDATE "InternetArchiveCaptures" SET "Status"=@status,"Sha256"=@hash,"ProcessedAtUtc"=NOW(),
                "Points"=@points,"Conflicts"=@conflicts,"LastError"=NULL
            WHERE "ItemId"=@id AND "ReplayUrl"=@url
            AND ("Status","Sha256","Points","Conflicts","LastError")
                IS DISTINCT FROM (@status,@hash,@points,@conflicts,NULL::text)
            """,("status",conflicts>0?"GuardadaConDiferencias":points.Count==0?"SinSerie":"Guardada"),("hash",hash),
                ("points",points.Count),("conflicts",conflicts),("id",job.ItemId),("url",capture.ReplayUrl));await complete.ExecuteNonQueryAsync(ct);
        return (added,conflicts);
    }
    public async Task ErrorAsync(ArchiveJob job,string? url,string code,CancellationToken ct)
    {
        var sql=url==null?"""
            UPDATE "InternetArchiveJobs" SET "Status"='Error',"Attempts"="Attempts"+1,"NextAttemptAtUtc"=NOW()+INTERVAL '15 minutes',
                "LastAttemptAtUtc"=NOW(),"LastError"=@code WHERE "ItemId"=@id
            """:"""
            UPDATE "InternetArchiveCaptures" SET "Status"='Error',"Attempts"="Attempts"+1,"NextAttemptAtUtc"=NOW()+INTERVAL '15 minutes',
                "LastError"=@code WHERE "ItemId"=@id AND "ReplayUrl"=@url
            """;
        await using var c=Cmd(sql,("id",job.ItemId),("code",code));if(url!=null)c.Parameters.AddWithValue("url",url);await c.ExecuteNonQueryAsync(ct);
    }
    public async Task<TimeSpan?> NextRetryAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            SELECT GREATEST(INTERVAL '1 second',MIN(t)-NOW()) FROM (
             SELECT "NextAttemptAtUtc" t FROM "InternetArchiveJobs" WHERE NOT "IndexCompleted" AND "Attempts"<3
             UNION ALL SELECT "NextAttemptAtUtc" FROM "InternetArchiveCaptures" WHERE "Status" IN ('Pendiente','Error') AND "Attempts"<3
            ) q HAVING COUNT(*)>0
            """);return await c.ExecuteScalarAsync(ct) as TimeSpan?;
    }
    public async Task StatusAsync(CancellationToken ct)
    {
        await using var c=Cmd("""
            SELECT 'Artículos' AS kind,"Status",COUNT(*) FROM "InternetArchiveJobs" GROUP BY "Status"
            UNION ALL SELECT 'Capturas',"Status",COUNT(*) FROM "InternetArchiveCaptures" GROUP BY "Status"
            UNION ALL SELECT 'Puntos','Importados',COUNT(*) FROM "MarketHistoryInternetArchivePoints"
            """);await using var r=await c.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))Console.WriteLine($"{r.GetString(0)} / {r.GetString(1)}: {r.GetInt64(2)}");
    }
}
public sealed class ArchiveDisabledException : Exception;
