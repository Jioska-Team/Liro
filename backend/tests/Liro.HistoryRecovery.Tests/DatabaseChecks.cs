using Liro.HistoryRecovery;
using Npgsql;

internal static class DatabaseChecks
{
    public static async Task RunAsync()
    {
        var connection=Environment.GetEnvironmentVariable("LIRO_HISTORY_TEST_CONNECTION") ?? throw new Exception("Falta conexión de pruebas.");
        var schema="liro_history_test_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE SCHEMA {schema}",admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var builder=new NpgsqlConnectionStringBuilder(connection) { SearchPath=schema,Pooling=false,CommandTimeout=5 };
            await using var db=new NpgsqlConnection(builder.ConnectionString);await db.OpenAsync();
            await Execute(db,"""
                CREATE TABLE "Items" ("Id" uuid PRIMARY KEY,"RobloxAssetId" bigint,"ItemType" text,"CreatorId" bigint,"CreatorType" text,"MarketStatus" text,"CollectibleItemId" uuid);
                CREATE TABLE "MarketHistoryPoints" ("ItemId" uuid,"Source" text,"Metric" text,"PeriodUtc" timestamptz,"Value" numeric,"ObservedAtUtc" timestamptz,
                PRIMARY KEY("ItemId","Source","Metric","PeriodUtc"));
                INSERT INTO "Items" VALUES ('00000000-0000-0000-0000-000000000001',215718515,'Asset',1,'User','Limited',NULL);
                """);
            var store=new RecoveryStore(db);await store.InitializeAsync(default);await store.SeedAsync(default);
            if(!await store.AcquireAsync(default)) throw new Exception("No adquirió exclusión.");
            await using var other=new NpgsqlConnection(builder.ConnectionString);await other.OpenAsync();
            if(await new RecoveryStore(other).AcquireAsync(default)) throw new Exception("Se permitió otra instancia.");
            if((await store.DownloadStateAsync(default)).Allowed || await store.ReserveDownloadAsync(default) != null) throw new Exception("Descargas activas sin configuración.");
            var id=Guid.Parse("00000000-0000-0000-0000-000000000001");
            var days=new[]{new HistoryDay(new(2016,9,4,0,0,0,DateTimeKind.Utc),7539,60)};
            if(await store.SaveAsync(id,days,DateTime.UtcNow,default)!=2) throw new Exception("No insertó el par de métricas.");
            if(await store.SaveAsync(id,days,DateTime.UtcNow,default)!=0) throw new Exception("Duplicó al repetir.");
            await Execute(db,"""
                INSERT INTO "MarketHistoryPoints" VALUES ('00000000-0000-0000-0000-000000000001','marketplace-sales','PrecioMedioVenta','2016-09-04Z',7540,NOW());
                """);
            if(await Scalar(db,"SELECT COUNT(*) FROM \"MarketHistoryCombined\"")!=2 || await Scalar(db,"SELECT \"Value\" FROM \"MarketHistoryCombined\" WHERE \"Metric\"='PrecioMedioVenta'")!=7540) throw new Exception("La consulta combinada duplica o no prioriza Roblox.");
            if(await store.ConflictsAsync(id,days,default)!=1) throw new Exception("No detectó discrepancia.");
            // A held archive row must abort promptly instead of waiting behind another writer.
            await using(var tx=await other.BeginTransactionAsync())
            {
                await using(var cmd=new NpgsqlCommand("UPDATE \"MarketHistoryArchivePoints\" SET \"Value\"=61 WHERE \"Metric\"='VolumenVentas'",other,tx)) await cmd.ExecuteNonQueryAsync();
                var blocked=false;
                try { await store.SaveAsync(id,days,DateTime.UtcNow,default); } catch(PostgresException e) when(e.SqlState=="55P03") { blocked=true; }
                if(!blocked) throw new Exception("No cedió ante el bloqueo.");
                await tx.RollbackAsync();
            }
            var more=Enumerable.Range(1,121).Select(n=>new HistoryDay(days[0].DateUtc.AddDays(n),100,1)).ToArray();
            using(var stop=new CancellationTokenSource())
            {
                stop.CancelAfter(100);
                try { await store.SaveAsync(id,more,DateTime.UtcNow,stop.Token); } catch(OperationCanceledException) { }
            }
            await store.SaveAsync(id,more,DateTime.UtcNow,default);
            if(await Scalar(db,"SELECT COUNT(*) FROM \"MarketHistoryCombined\"")!=244) throw new Exception("Reanudación incompleta o duplicada.");
            if(await store.SaveAsync(id,more,DateTime.UtcNow,default)!=0) throw new Exception("Repetición después de reanudar duplicada.");
            var archive=new InternetArchiveStore(db);await archive.SeedAsync(default);
            var job=await archive.FindAsync(215718515,"Asset",default)??throw new Exception("Falta trabajo de archivo.");
            using(var payload=System.Text.Json.JsonDocument.Parse("""{"priceDataPoints":[{"date":"2018-01-01T06:00:00Z","value":5},{"date":"2018-01-02T06:00:00Z","value":7}],"volumeDataPoints":[{"date":"2018-01-02T06:00:00Z","value":2}]}"""))
            {
                var capture=new ArchiveCapture(215718515,"Asset",job.Routes[0],new(2019,1,1,0,0,0,DateTimeKind.Utc),DateTime.UtcNow,payload.RootElement.Clone());
                await archive.EnqueueAsync(job,[new(capture.OriginalUrl,capture.CapturedAtUtc,"")],default);
                await Execute(db,"""INSERT INTO "MarketHistoryPoints" VALUES ('00000000-0000-0000-0000-000000000001','marketplace-sales','PrecioMedioVenta','2018-01-02T18:00:00Z',8,NOW())""");
                var first=await archive.SaveAsync(job,capture,"prueba",default);
                if(first.Added!=2 || first.Conflicts!=1)throw new Exception("No conserva métricas independientes o no respeta el día UTC.");
                if((await archive.SaveAsync(job,capture,"prueba",default)).Added!=0)throw new Exception("Duplicó una captura.");
                if(await Scalar(db,"SELECT EXTRACT(HOUR FROM \"OriginalPeriodUtc\" AT TIME ZONE 'UTC') FROM \"MarketHistoryInternetArchivePoints\" LIMIT 1")!=6)throw new Exception("Perdió la marca original.");
                await Execute(db,"""INSERT INTO "MarketHistoryPoints" VALUES ('00000000-0000-0000-0000-000000000001','marketplace-sales','PrecioMedioVenta','2018-01-01T05:00:00Z',10,NOW())""");
                if(await Scalar(db,"SELECT COUNT(*) FROM \"MarketHistoryCombined\" WHERE \"PeriodUtc\"='2018-01-01Z'")!=1
                    || await Scalar(db,"SELECT \"Value\" FROM \"MarketHistoryCombined\" WHERE \"PeriodUtc\"='2018-01-01Z'")!=10)throw new Exception("No prevalece Roblox al llegar después a otra hora.");
                var page=new ArchiveIndexPage([new(capture.OriginalUrl,capture.CapturedAtUtc,"")],"cursor");
                await archive.AdvanceAsync(job,page,default);
                var resumed=await archive.FindAsync(215718515,"Asset",default);
                if(resumed?.ResumeKey!="cursor" || resumed.RouteIndex!=0)throw new Exception("Perdió el cursor de búsqueda.");
                await archive.AdvanceAsync(resumed,new([],null),default);
                if((await archive.FindAsync(215718515,"Asset",default))?.RouteIndex!=1)throw new Exception("No avanzó a la siguiente ruta.");
            }
            // Sleeping queries model occupancy without consuming CPU or modifying live data.
            var occupied=new List<NpgsqlConnection>();
            var commands=new List<NpgsqlCommand>();
            var waits=new List<Task<int>>();
            try
            {
                for(var n=0;n<4;n++) {
                    var occupiedConnection=new NpgsqlConnection(builder.ConnectionString);
                    await occupiedConnection.OpenAsync();occupied.Add(occupiedConnection);
                    var command=new NpgsqlCommand("SELECT pg_sleep(4)",occupiedConnection);commands.Add(command);
                    waits.Add(command.ExecuteNonQueryAsync());
                }
                await Task.Delay(100);
                var yielded=false;
                try { await store.SaveAsync(id,more,DateTime.UtcNow,default); } catch(RecoveryBusyException) { yielded=true; }
                if(!yielded) throw new Exception("No cedió ante consultas activas.");
            }
            finally
            {
                await Task.WhenAll(waits);
                foreach(var command in commands) await command.DisposeAsync();
                foreach(var occupiedConnection in occupied) await occupiedConnection.DisposeAsync();
            }
        }
        finally
        {
            // Only the unique schema created by this test is removed; public is never in its search_path.
            await using var cleanup=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",admin); await cleanup.ExecuteNonQueryAsync();
        }
    }
    private static async Task Execute(NpgsqlConnection db,string sql) { await using var c=new NpgsqlCommand(sql,db);await c.ExecuteNonQueryAsync(); }
    private static async Task<decimal> Scalar(NpgsqlConnection db,string sql) { await using var c=new NpgsqlCommand(sql,db);return Convert.ToDecimal(await c.ExecuteScalarAsync()); }
}
