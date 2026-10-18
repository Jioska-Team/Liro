using Npgsql;
namespace Liro.HistoryRecovery;

public sealed record RecoveryJob(Guid ItemId, long RobloxId, string ItemType);
public sealed class RecoveryBusyException : Exception;
public sealed class RecoveryStore(NpgsqlConnection db)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await using(var limits = new NpgsqlCommand("SET LOCAL lock_timeout='2s'; SET LOCAL statement_timeout='10s';",db,tx)) await limits.ExecuteNonQueryAsync(ct);
        using var stream = typeof(RecoveryStore).Assembly.GetManifestResourceStream("Liro.HistoryRecovery.schema.sql")!;
        using var reader = new StreamReader(stream);
        await using var cmd = new NpgsqlCommand(await reader.ReadToEndAsync(ct), db,tx);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<bool> AcquireAsync(CancellationToken ct)
    {
        // Session-level: released automatically on connection loss or process exit.
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(716289430154::bigint)",db);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task SeedAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "HistoryRecoveryJobs" ("ItemId")
            SELECT "Id" FROM "Items" WHERE "CreatorId"=1 AND "CreatorType"='User'
            AND "MarketStatus" IN ('Limited','LimitedUnique') AND "ItemType" IN ('Asset','Bundle')
            ON CONFLICT DO NOTHING
            """,db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<List<RecoveryJob>> JobsAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT j."ItemId",i."RobloxAssetId",i."ItemType" FROM "HistoryRecoveryJobs" j
            JOIN "Items" i ON i."Id"=j."ItemId"
            WHERE j."Status" IN ('Pendiente','Error') AND j."Attempts"<3 AND j."NextAttemptAtUtc"<=NOW()
            AND i."CreatorId"=1 AND i."CreatorType"='User' AND i."MarketStatus" IN ('Limited','LimitedUnique')
            ORDER BY j."NextAttemptAtUtc",j."ItemId"
            """,db);
        var result = new List<RecoveryJob>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result.Add(new(r.GetGuid(0),r.GetInt64(1),r.GetString(2)));
        return result;
    }
    public async Task<DateTime?> ReserveDownloadAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE "HistoryRecoveryControl" SET "NextDownloadAtUtc"=NOW()+INTERVAL '15 seconds'
            WHERE "Id" AND "AllowRemoteDownloads" AND length(trim("PermissionReference"))>0
            AND "NextDownloadAtUtc"<=NOW() RETURNING "NextDownloadAtUtc"
            """,db);
        return await cmd.ExecuteScalarAsync(ct) as DateTime?;
    }
    public async Task<(bool Allowed, TimeSpan Delay)> DownloadStateAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT "AllowRemoteDownloads" AND length(trim("PermissionReference"))>0,
            GREATEST(INTERVAL '0 seconds',"NextDownloadAtUtc"-NOW()) FROM "HistoryRecoveryControl" WHERE "Id"
            """,db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return (false,TimeSpan.Zero);
        return (r.GetBoolean(0),r.GetTimeSpan(1));
    }
    public async Task CooldownAsync(TimeSpan duration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE "HistoryRecoveryControl" SET "NextDownloadAtUtc"=GREATEST("NextDownloadAtUtc",NOW()+@delay) WHERE "Id"
            """,db);
        cmd.Parameters.AddWithValue("delay",duration); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<int> ConflictsAsync(Guid id,IReadOnlyList<HistoryDay> days,CancellationToken ct)
    {
        var points = Points(days);
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*)::int FROM "MarketHistoryPoints" h
            JOIN unnest(@metrics::text[],@dates::timestamptz[],@values::numeric[]) p(metric,period,value)
              ON h."Metric"=p.metric AND h."PeriodUtc"=p.period
            WHERE h."ItemId"=@id AND h."Source"='marketplace-sales' AND h."Value"<>p.value
            """,db);
        Parameters(cmd,id,points); return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private static (string Metric,DateTime Date,decimal Value)[] Points(IEnumerable<HistoryDay> days) => days.SelectMany(d => new[] {
        ("PrecioMedioVenta",d.DateUtc,d.AveragePrice),("VolumenVentas",d.DateUtc,(decimal)d.Volume) }).ToArray();
    private static void Parameters(NpgsqlCommand cmd, Guid id,(string Metric,DateTime Date,decimal Value)[] points)
    {
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("metrics",points.Select(x=>x.Metric).ToArray());
        cmd.Parameters.AddWithValue("dates",points.Select(x=>x.Date).ToArray());
        cmd.Parameters.AddWithValue("values",points.Select(x=>x.Value).ToArray());
    }
    public async Task<int> SaveAsync(Guid id,IReadOnlyList<HistoryDay> days,DateTime fetchedAt,CancellationToken ct)
    {
        var inserted=0;
        foreach(var batch in Points(days).Chunk(120))
        {
            // Yield to live work rather than competing when the database is busy.
            await using(var capacity=new NpgsqlCommand("""
                SELECT COUNT(*) FILTER (WHERE state='active')>=4
                   OR COUNT(*) FILTER (WHERE wait_event_type='Lock')>0
                FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid()
                """,db))
                if((bool)(await capacity.ExecuteScalarAsync(ct))!) throw new RecoveryBusyException();
            await using var tx = await db.BeginTransactionAsync(ct);
            await using(var limits=new NpgsqlCommand("SET LOCAL lock_timeout='250ms'; SET LOCAL statement_timeout='3s';",db,tx)) await limits.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "MarketHistoryArchivePoints" ("ItemId","Metric","PeriodUtc","Value","ObservedAtUtc")
                SELECT @id,p.metric,p.period,p.value,@observed
                FROM unnest(@metrics::text[],@dates::timestamptz[],@values::numeric[]) p(metric,period,value)
                WHERE NOT EXISTS (SELECT 1 FROM "MarketHistoryPoints" h
                  WHERE h."ItemId"=@id AND h."Metric"=p.metric AND h."PeriodUtc"=p.period)
                ON CONFLICT ("ItemId","Metric","PeriodUtc") DO NOTHING
                """,db,tx);
            Parameters(cmd,id,batch); cmd.Parameters.AddWithValue("observed",fetchedAt);
            inserted+=await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            await Task.Delay(1000,ct);
        }
        return inserted;
    }
    public async Task CompleteAsync(Guid id,CachedHistory cache,IReadOnlyList<HistoryDay> days,string hash,int conflicts,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            UPDATE "HistoryRecoveryJobs" SET "Status"=@status,"CompletedAtUtc"=NOW(),"SourceUrl"=@url,"Sha256"=@hash,
            "FirstDateUtc"=@first,"LastDateUtc"=@last,"SourceDays"=@days,"ConflictPoints"=@conflicts,"LastError"=NULL
            WHERE "ItemId"=@id
            """,db);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("status",conflicts>0?"Revisar":days.Count==0?"SinDatos":"Completado");
        cmd.Parameters.AddWithValue("url",cache.SourceUrl);cmd.Parameters.AddWithValue("hash",hash);
        cmd.Parameters.Add(new("first",NpgsqlTypes.NpgsqlDbType.TimestampTz){Value=days.Count>0?days[0].DateUtc:DBNull.Value});
        cmd.Parameters.Add(new("last",NpgsqlTypes.NpgsqlDbType.TimestampTz){Value=days.Count>0?days[^1].DateUtc:DBNull.Value});
        cmd.Parameters.AddWithValue("days",days.Count);cmd.Parameters.AddWithValue("conflicts",conflicts);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task FailureAsync(Guid id,string code,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            UPDATE "HistoryRecoveryJobs" SET "Status"='Error',"Attempts"="Attempts"+1,
            "LastError"=@code,"NextAttemptAtUtc"=NOW()+INTERVAL '15 minutes' WHERE "ItemId"=@id
            """,db);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("code",code);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task StatusAsync(CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT "Status",COUNT(*),SUM("SourceDays"),SUM("ConflictPoints") FROM "HistoryRecoveryJobs" GROUP BY "Status" ORDER BY "Status"
            """,db);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) Console.WriteLine($"{r.GetString(0)}: {r.GetInt64(1)} artículos; {r.GetInt64(2)} fechas de fuente; {r.GetInt64(3)} discrepancias.");
    }
}
