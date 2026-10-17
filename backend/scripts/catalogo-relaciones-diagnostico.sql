-- Avance de la composición de los paquetes.
SELECT COUNT(*) AS "Paquetes",
       COUNT(*) FILTER (WHERE "BundleComponentsObservedAtUtc" IS NOT NULL) AS "ConComposicionConsultada"
FROM "Items" WHERE "ItemType" = 'Bundle';

SELECT COUNT(*) AS "Relaciones", COUNT(DISTINCT "ComponentItemId") AS "ComponentesDistintos"
FROM "BundleComponents";

-- Sin filas significa que no hay artículos duplicados.
SELECT "ItemType", "RobloxAssetId", COUNT(*) AS "Repeticiones"
FROM "Items" GROUP BY "ItemType", "RobloxAssetId" HAVING COUNT(*) > 1;

-- Sin filas significa que no hay relaciones duplicadas.
SELECT "BundleId", "ComponentItemId", COUNT(*) AS "Repeticiones"
FROM "BundleComponents" GROUP BY "BundleId", "ComponentItemId" HAVING COUNT(*) > 1;

-- Componentes todavía no confirmados/importados (sin inventar registros).
SELECT b."RobloxAssetId" AS "PaqueteRoblox", p.id AS "ComponentePendiente"
FROM "Items" b CROSS JOIN LATERAL unnest(b."BundleComponentAssetIds") p(id)
WHERE b."ItemType" = 'Bundle'
AND NOT EXISTS (SELECT 1 FROM "Items" c WHERE c."ItemType" = 'Asset' AND c."RobloxAssetId" = p.id);
