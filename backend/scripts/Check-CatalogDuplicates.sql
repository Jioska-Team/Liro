-- PostgreSQL. Solo lectura: no elimina ni modifica registros.
-- La identidad es (ItemType, RobloxAssetId). Asset 123 y Bundle 123 son distintos.
BEGIN TRANSACTION READ ONLY;

-- 1. Duplicados reales. Cero filas significa que no hay identidades repetidas.
SELECT "ItemType", "RobloxAssetId", COUNT(*) AS "Cantidad",
       ARRAY_AGG("Id" ORDER BY "Id") AS "IdsInternos"
FROM "Items"
GROUP BY "ItemType", "RobloxAssetId"
HAVING COUNT(*) > 1
ORDER BY "Cantidad" DESC, "ItemType", "RobloxAssetId";

-- 2. Resumen: RegistrosSobrantes debe ser 0.
WITH grupos AS (
    SELECT COUNT(*) AS cantidad
    FROM "Items"
    GROUP BY "ItemType", "RobloxAssetId"
)
SELECT COALESCE(SUM(cantidad), 0) AS "TotalRegistros",
       COUNT(*) AS "IdentidadesUnicas",
       COUNT(*) FILTER (WHERE cantidad > 1) AS "GruposDuplicados",
       COALESCE(SUM(cantidad - 1), 0) AS "RegistrosSobrantes"
FROM grupos;

-- 3. Comprobacion adicional: CollectibleItemId no nulo repetido.
SELECT "CollectibleItemId", COUNT(*) AS "Cantidad",
       ARRAY_AGG("Id" ORDER BY "Id") AS "IdsInternos"
FROM "Items"
WHERE "CollectibleItemId" IS NOT NULL
GROUP BY "CollectibleItemId"
HAVING COUNT(*) > 1;

COMMIT;
