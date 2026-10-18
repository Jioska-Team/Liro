using Liro.HistoryRecovery;
using Npgsql;
using System.Text.Json;
internal static class SchedulingChecks
{
 public static async Task RunAsync()
 {
  var connection=Environment.GetEnvironmentVariable("LIRO_HISTORY_TEST_CONNECTION")??throw new Exception("Falta conexión de prueba");
  var schema="liro_schedule_test_"+Guid.NewGuid().ToString("N");
  await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
  await Exec(admin,$"CREATE SCHEMA {schema}");
  try {
   var builder=new NpgsqlConnectionStringBuilder(connection){SearchPath=schema,Pooling=false,CommandTimeout=5};
   await using var db=new NpgsqlConnection(builder.ConnectionString);await db.OpenAsync();
   await Exec(db,"""
    CREATE TABLE "Items" ("Id" uuid PRIMARY KEY,"RobloxAssetId" bigint,"ItemType" text,"CreatorId" bigint,"CreatorType" text,"MarketStatus" text,"CollectibleItemId" uuid);
    CREATE TABLE "MarketHistoryPoints" ("ItemId" uuid,"Source" text,"Metric" text,"PeriodUtc" timestamptz,"Value" numeric,"ObservedAtUtc" timestamptz);
    INSERT INTO "Items" SELECT ('00000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,n,'Asset',1,'User','Limited',NULL FROM generate_series(1,3) n;
    """);
   var recovery=new RecoveryStore(db);await recovery.InitializeAsync(default);await recovery.SeedAsync(default);
   var store=new InternetArchiveStore(db);await store.SeedAsync(default);
   var a=(await store.FindAsync(1,"Asset",default))!;var b=(await store.FindAsync(2,"Asset",default))!;var c=(await store.FindAsync(3,"Asset",default))!;
   foreach(var job in new[]{a,b,c})await store.EnqueueAsync(job,Enumerable.Range(1,3).Select(n=>new ArchiveIndexRow(job.Routes[0],new DateTime(2019,1,n,0,0,0,DateTimeKind.Utc),"")).ToArray(),default);
   // Live history and older imports count; a RAP snapshot does not constitute sales history.
   await Exec(db,"""
    INSERT INTO "MarketHistoryPoints" VALUES
      ('00000000-0000-0000-0000-000000000001','marketplace-sales','PrecioMedioVenta','2022-01-01Z',5,NOW()),
      ('00000000-0000-0000-0000-000000000002','marketplace-sales','RAP','2022-01-01Z',5,NOW());
    INSERT INTO "MarketHistoryArchivePoints" ("ItemId","Metric","PeriodUtc","Value","ObservedAtUtc") VALUES
      ('00000000-0000-0000-0000-000000000003','VolumenVentas','2018-01-01Z',1,NOW());
    """);
   if((await store.NextJobAsync(default))!.ItemId!=b.ItemId)throw new Exception("El índice no prioriza al artículo sin historial de ventas en ninguna fuente");
   if((await store.NextCaptureAsync(default))!.Job.ItemId!=b.ItemId)throw new Exception("Las capturas confunden ausencia en Internet Archive con ausencia en todas las fuentes");
   await store.AdvanceAsync(b,new([],null),default);
   if((await new InternetArchiveStore(db).NextJobAsync(default))!.ItemId==b.ItemId)throw new Exception("La prioridad impide avanzar a los demás índices de la vuelta");
   await Exec(db,"""DELETE FROM "MarketHistoryPoints"; DELETE FROM "MarketHistoryArchivePoints";""");
   using var payload=JsonDocument.Parse("""{"priceDataPoints":[{"date":"2018-01-01T06:00:00Z","value":5}],"volumeDataPoints":[]}""");
   ArchiveCapture Capture(ArchiveJob job,int day)=>new(job.RobloxId,"Asset",job.Routes[0],new(2019,1,day,0,0,0,DateTimeKind.Utc),DateTime.UtcNow,payload.RootElement.Clone());
   await Exec(db,"""INSERT INTO "MarketHistoryInternetArchivePoints" VALUES ('00000000-0000-0000-0000-000000000001','PrecioMedioVenta','2018-01-01Z','2018-01-01T06:00Z',5,'roblox-internet-archive',NOW(),'2019-01-01Z','prueba','hash')""");
   if((await store.NextCaptureAsync(default))!.Job.ItemId!=b.ItemId)throw new Exception("No prioriza el artículo sin historial dentro de la vuelta");
   await store.SaveAsync(b,Capture(b,1),"hash",default);
   if((await new InternetArchiveStore(db).NextCaptureAsync(default))!.Job.ItemId!=c.ItemId)throw new Exception("La reanudación no reparte el turno al siguiente artículo sin historial");
   await store.SaveAsync(c,Capture(c,1),"hash",default);
   if((await store.NextCaptureAsync(default))!.Job.ItemId!=a.ItemId)throw new Exception("Dejó atrás un artículo que ya tenía historial");
   await store.SaveAsync(a,Capture(a,1),"hash",default);
   // An empty capture consumes its turn, too, instead of monopolizing the queue.
   await Exec(db,"""DELETE FROM "MarketHistoryInternetArchivePoints" WHERE "ItemId"='00000000-0000-0000-0000-000000000002'""");
   using var empty=JsonDocument.Parse("""{"priceDataPoints":[],"volumeDataPoints":[]}""");
   await store.SaveAsync(b,Capture(b,2) with {Payload=empty.RootElement.Clone()},"empty",default);
   if((await store.NextCaptureAsync(default))!.Job.ItemId==b.ItemId)throw new Exception("Una captura vacía acaparó los turnos");
   await store.ErrorAsync(c,Capture(c,2).ReplayUrl,"ErrorConexion",default);
   if((await store.NextCaptureAsync(default))!.Capture.ReplayUrl==Capture(c,2).ReplayUrl)throw new Exception("Ignoró la espera del reintento");
   // Reject any INSERT statement and any changed capture row during a no-op replay.
   await Exec(db,"""
    CREATE FUNCTION reject_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Escritura innecesaria'; END $$;
    CREATE TRIGGER no_insert BEFORE INSERT ON "MarketHistoryInternetArchivePoints" FOR EACH STATEMENT EXECUTE FUNCTION reject_write();
    CREATE TRIGGER no_update BEFORE UPDATE ON "InternetArchiveCaptures" FOR EACH ROW EXECUTE FUNCTION reject_write();
    """);
   var repeated=await store.SaveAsync(a,Capture(a,1),"hash",default);
   if(repeated.Added!=0||repeated.Conflicts!=0)throw new Exception("Repetición no idempotente");
   await Exec(db,"""DROP TRIGGER no_insert ON "MarketHistoryInternetArchivePoints"; DROP TRIGGER no_update ON "InternetArchiveCaptures";""");
   using var mixed=JsonDocument.Parse("""{"priceDataPoints":[{"date":"2018-01-01T06:00:00Z","value":9},{"date":"2018-01-02T06:00:00Z","value":7}],"volumeDataPoints":[{"date":"2018-01-01T06:00:00Z","value":2}]}""");
   var saved=await store.SaveAsync(a,Capture(a,2) with{Payload=mixed.RootElement.Clone()},"mixed",default);
   if(saved.Added!=2||saved.Conflicts!=1)throw new Exception("Perdió una métrica faltante o sobrescribió una diferencia");
   if((await store.SaveAsync(a,Capture(a,2) with{Payload=mixed.RootElement.Clone()},"mixed",default)).Added!=0)throw new Exception("Duplicó la captura mixta");
   await store.EnableAsync(true,default);
   await store.ReserveRequestAsync(default);
   await using(var check=new NpgsqlCommand("""SELECT EXTRACT(EPOCH FROM "NextRequestAtUtc"-"LastRequestAtUtc") FROM "InternetArchiveControl" """,db))
    if(Convert.ToDecimal(await check.ExecuteScalarAsync())!=15)throw new Exception("Cambió el intervalo de solicitudes");
   using(var canceled=new CancellationTokenSource(100)) {
    var stopped=false;
    try{await store.ReserveRequestAsync(canceled.Token);}catch(OperationCanceledException){stopped=true;}
    if(!stopped)throw new Exception("Permitió otra solicitud antes de los 15 segundos");
   }
  } finally {await Exec(admin,$"DROP SCHEMA {schema} CASCADE");}
 }
 private static async Task Exec(NpgsqlConnection db,string sql){await using var cmd=new NpgsqlCommand(sql,db);await cmd.ExecuteNonQueryAsync();}
}


