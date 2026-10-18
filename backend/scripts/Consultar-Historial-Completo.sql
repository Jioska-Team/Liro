-- Cambiar únicamente el ID y el tipo (Asset o Bundle).
SELECT i."RobloxAssetId" AS "IdRoblox", i."Name" AS "Articulo",
       (h."PeriodUtc" AT TIME ZONE 'UTC')::date AS "FechaUTC",
       MAX(h."Value") FILTER (WHERE h."Metric"='PrecioMedioVenta') AS "PrecioMedioVentaRobux",
       MAX(h."Value") FILTER (WHERE h."Metric"='VolumenVentas') AS "CantidadVendida",
       STRING_AGG(DISTINCT h."Source", ', ') AS "Fuente"
FROM "MarketHistoryCombined" h JOIN "Items" i ON i."Id"=h."ItemId"
WHERE i."RobloxAssetId"=215718515 AND i."ItemType"='Asset'
GROUP BY i."RobloxAssetId",i."Name",(h."PeriodUtc" AT TIME ZONE 'UTC')::date
ORDER BY "FechaUTC";
