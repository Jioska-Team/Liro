SELECT "Status" AS "Estado", COUNT(*) AS "Articulos",
       SUM("SourceDays") AS "FechasEnLaFuente", SUM("ConflictPoints") AS "Discrepancias",
       MAX("CompletedAtUtc") AS "UltimoProcesado"
FROM "HistoryRecoveryJobs" GROUP BY "Status" ORDER BY "Status";

SELECT "AllowRemoteDownloads" AS "DescargasHabilitadas",
       "PermissionReference" AS "ReferenciaAutorizacion", "NextDownloadAtUtc" AS "SiguienteDescarga"
FROM "HistoryRecoveryControl";

SELECT COUNT(*) AS "PuntosArchivados",COUNT(DISTINCT "ItemId") AS "ArticulosConArchivo",
       MIN("PeriodUtc") AS "PrimeraFecha", MAX("PeriodUtc") AS "UltimaFecha"
FROM "MarketHistoryArchivePoints";

SELECT i."RobloxAssetId",i."Name",i."ItemType",j."Status",j."LastError",j."Attempts",j."ConflictPoints"
FROM "HistoryRecoveryJobs" j JOIN "Items" i ON i."Id"=j."ItemId"
WHERE j."Status" IN ('Error','Revisar') ORDER BY j."Attempts" DESC;

-- Recuperación independiente de Internet Archive.
SELECT "Enabled" AS "Activo", "LastRequestAtUtc" AS "UltimaSolicitudUTC",
       "NextRequestAtUtc" AS "ProximaSolicitudUTC", "LastError" AS "UltimoError"
FROM "InternetArchiveControl";
SELECT "Status" AS "Estado", COUNT(*) AS "Articulos"
FROM "InternetArchiveJobs" GROUP BY "Status" ORDER BY "Status";
SELECT "Status" AS "Estado", COUNT(*) AS "Capturas", SUM("Points") AS "PuntosProcesados"
FROM "InternetArchiveCaptures" GROUP BY "Status" ORDER BY "Status";
SELECT COUNT(*) AS "PuntosRecuperados", COUNT(DISTINCT "ItemId") AS "ArticulosConPuntos",
       MIN("PeriodUtc") AS "PrimeraFechaUTC", MAX("PeriodUtc") AS "UltimaFechaUTC"
FROM "MarketHistoryInternetArchivePoints";
