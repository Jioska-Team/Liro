SELECT COUNT(*) AS "ArticulosEnSeguimiento",
       COUNT(*) FILTER (WHERE "LastSuccessAtUtc" IS NOT NULL) AS "Consultados",
       COUNT(*) FILTER (WHERE "NextCheckAtUtc" <= NOW()) AS "PendientesPorFecha",
       COUNT(*) FILTER (WHERE "LeaseUntilUtc">NOW()) AS "Reservados",
       COUNT(*) FILTER (WHERE "ConsecutiveFailures">0) AS "ConReintentos",
       MIN("RapObservedAtUtc") AS "RapMasAntiguo",
       MAX("LastSuccessAtUtc") AS "UltimaRespuesta"
FROM "MarketWatches";

SELECT i."RobloxAssetId" AS "IdRoblox",i."Name" AS "Nombre",i."MarketplaceUrl" AS "UrlRoblox",
       w."RecentAveragePrice" AS "RAP",w."RapObservedAtUtc" AS "FechaRAP",
       w."Activity" AS "ActividadDeVentas",w."DailySales" AS "PromedioDiario7Dias",
       w."NextCheckAtUtc" AS "ProximaRevision",w."LastErrorCode" AS "UltimoError"
FROM "MarketWatches" w JOIN "Items" i ON i."Id"=w."ItemId"
ORDER BY w."NextCheckAtUtc" LIMIT 100;

SELECT "Metric" AS "Indicador",COUNT(*) AS "Puntos",MIN("PeriodUtc") AS "PrimerPeriodo",MAX("PeriodUtc") AS "UltimoPeriodo"
FROM "MarketHistoryPoints" GROUP BY "Metric";

-- Configuración sin exponer dirección ni secretos.
SELECT "Tipo","Clave","Activo","Descripcion","FechaModificacion"
FROM "CatalogoGeneral" WHERE "Tipo"='CONEXION_MERCADO';
