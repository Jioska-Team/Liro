# Catalogo oficial: assets y bundles

El descubrimiento usa v2/search/items/details, IncludeNotForSale=true y paginas
configurables (120 por defecto). Mantiene dos recorridos independientes:
SalesTypeFilter=1 (todos) y 2 (collectibles, que no equivale exclusivamente a limiteds).
El importador vuelve a verificar CreatorType=User y CreatorId=1 antes de guardar.
No hay un limite local de 828 items. La cobertura depende de lo que Roblox devuelve
en su busqueda y paginacion; no se garantiza enumerar todos sus objetos existentes.

## Activacion

Detener las instancias antiguas de API/workers/AppHost antes de iniciar la version
actualizada. En Development la inicializacion aplica las migraciones pendientes (incluidas AddCatalogItemTypes y AddCatalogCoverageProgress).
En otros entornos se deben aplicar las migraciones antes de iniciar los servicios.
No borrar Items, historial ni CatalogCheckpoints. La migracion clasifica los
registros previos como Asset y conserva sus UUID; permite un Bundle con igual ID.
MarketplaceUrl se recalcula como columna generada. El rollback se rechaza si ya
hay bundles para evitar perder su identidad.

Los checkpoints nuevos comienzan con catalog:v2:off-sale:typed y se separan por
bootstrap/recent y All/Collectibles. Los checkpoints antiguos se conservan pero
no bloquean este recorrido. Bootstrap reanuda su cursor al reiniciar. Recent
procesa hasta DiscoveryPages por alcance y ciclo; cuando hay cursor pendiente,
consulta ademas la primera pagina para detectar novedades sin esperar al final.
CycleInterval es una espera entre ciclos: el procesamiento y los limites HTTP
anaden tiempo. Los errores se guardan para reintento con identidad tipo + ID.

## Comprobacion en PostgreSQL

```sql
SELECT "ItemType", COUNT(*) FROM "Items" GROUP BY "ItemType";
SELECT "Scope", "Completed", "UpdatedAtUtc"
FROM "CatalogCheckpoints"
WHERE "Scope" LIKE 'catalog:v2:%'
ORDER BY "UpdatedAtUtc" DESC;
```

La columna fisica RobloxAssetId se conserva por compatibilidad y almacena el ID
numerico de ambos tipos. Usar siempre ItemType junto al ID al consultar SQL.
La API expone robloxItemId + itemType; robloxAssetId es null para bundles.
Un numero estancado requiere revisar los logs y CatalogImportFailures (403/429,
reintentos o dead letters), no borrar checkpoints ni reducir esperas a ciegas.

## Responsabilidades y rendimiento

Bootstrap y descubrimiento de novedades solo importan identidades ausentes.
Si el item ya existe, incluso sin fecha de comprobacion o con refresco vencido,
lo omiten sin consultar sus detalles/imagen en Roblox ni esperar ItemDelay.
CatalogRefreshWorker actualiza los existentes vencidos y procesa los reintentos
pendientes (que tambien pueden corresponder a altas fallidas).
Los dos procesos conservan el limitador HTTP compartido y el backoff de Roblox.

## Detalles por lotes

`CatalogWorkers:DetailsBatchSize` es 10 por defecto (configuracion local validada
entre 1 y 100). El limite de configuracion no afirma que Roblox acepte 100:
se verifico una peticion publica de 10 identidades, incluyendo un bundle, con
respuesta 200 y 10 resultados el 15 de octubre de 2026.

Bootstrap, novedades y actualizacion agrupan los IDs elegibles. Una identidad
es tipo + ID; la respuesta puede venir desordenada. Faltantes, duplicados y datos
invalidos quedan en reintentos individuales, conservando los resultados validos.
Si Roblox devuelve 400 con codigo 2 (Invalid count), el cliente divide el lote;
no divide ni dispara reintentos por articulo cuando recibe 429. Los errores de
peticion completa conservan el checkpoint. Cada guardado usa su propio scope.

La espera ItemDelay se aplica por lote procesado, no por articulo. El limitador
HTTP compartido sigue vigente para todos los envios. Las imagenes siguen siendo
consultas individuales y las solicitudes HTTP no se paralelizan en esta fase.
Los logs `Catalog details batch fetched` muestran cantidad, resultados validos y
tiempo de consulta; no equivalen al tiempo de guardado ni garantizan importacion.

Medicion determinista: 25 articulos nuevos requieren 3 consultas de detalles con
lotes de 10, frente a 25 individuales. No incluye desafios CSRF, imagenes ni
reintentos. Medir tiempos reales del recorrido antes de ampliar concurrencia.

## Cobertura historica por particiones (16 de octubre de 2026)

La consulta general verificada termino en 1000 resultados aunque una busqueda
por palabra encontraba articulos oficiales ausentes. Por eso el nuevo bootstrap
usa claves `catalog:coverage:v1:...` y no reutiliza como prueba de cobertura los
checkpoints historicos completados. Los registros y checkpoints previos se conservan.
Los recorridos recientes y de actualizacion mantienen sus responsabilidades.

Raices: busquedas generales, 40 tipos de assets, 4 tipos de bundles y palabras
complementarias configurables. La palabra inicial `valkyrie` permite reproducir
el caso reportado; no sustituye los recorridos por tipo. Se procesa una pagina
por particion y vuelta, con el limitador HTTP y lotes existentes.

Cuando una busqueda se agota tras recibir al menos `CoverageResultLimit` (1000),
se marca `PossiblyTruncated`. Se programan hijos por tipo si procede, rangos de
precio (0; 1-100; 101-1000; 1001-10000; 10001-100000; resto), bisecciones de
rangos saturados y ordenes complementarios por fecha/favoritos. La profundidad
esta limitada a `CoverageMaxDepth` (12). Un rango de precio unico no se divide
indefinidamente. Los articulos sin precio util pueden quedar fuera de filtros
de precio: se conservan las busquedas sin precio y estados de cobertura incierta.

`Completed=true` significa que se agoto ESE cursor. No significa catalogo completo.

| CoverageStatus | Significado |
| --- | --- |
| Unassessed | Checkpoint previo o todavia no evaluado |
| Running | Recorrido con mas paginas |
| SearchExhausted | Sin cursor, por debajo del umbral; sin garantia de cobertura total |
| PossiblyTruncated | Alcanzado el umbral; hijos programados cuando es posible, cobertura aun incierta |
| Failed | Fallo de consulta/procesamiento; cursor retenido y error visible |
| CursorReset | Cursor rechazado por proveedor; esa particion reinicia sus contadores |

Los estados de padres posiblemente truncados se conservan incluso despues de
terminar sus hijos. Los hijos se reconstruyen al reiniciar y reutilizan sus
propios checkpoints. Cambiar la lista de tipos/palabras requiere reiniciar workers.
Los tipos son configurables via `Roblox:Catalog:CoverageAssetTypeIds`,
`CoverageBundleTypeIds` y `CoverageKeywords`; no incluyen todos los tipos del
Creator Store. No se promete un total de 25000 ni todos los objetos de Roblox.

### Activacion y comprobacion

Detener las instancias antiguas antes de iniciar la nueva version. En Development
se aplica `AddCatalogCoverageProgress` automaticamente: solo agrega seis columnas
a CatalogCheckpoints; no borra items, historial ni cursores. En produccion aplicar
la migracion previamente. No ejecutar versiones viejas y nuevas simultaneamente.

```sql
SELECT "Scope", "CoverageStatus", "Completed", "PagesProcessed",
       "ItemsSeen", "ItemsAdded", "ItemFailures", "LastError", "UpdatedAtUtc"
FROM "CatalogCheckpoints"
WHERE "Scope" LIKE 'catalog:coverage:v1:%'
ORDER BY "UpdatedAtUtc" DESC;

SELECT "ItemType", COUNT(*) FROM "Items" GROUP BY "ItemType";

SELECT "RobloxAssetId", "Name", "CreatorId", "CreatorType"
FROM "Items"
WHERE "ItemType" = 'Asset'
  AND "RobloxAssetId" IN (17517206952, 7781687598, 928840419);
```

ItemsSeen cuenta resultados recibidos (no identidades globalmente unicas).
ItemsAdded cuenta altas realizadas por ese recorrido; ItemFailures cuenta fallos
individuales en paginas confirmadas, no el total actual de fallos pendientes.
Son metricas operativas por checkpoint, no un registro contable: una caida entre
el guardado de un item y el checkpoint puede subcontar altas; el reintento no
duplica el item. Para comparar el catalogo usar COUNT(*) antes/despues.

Validacion de proveedor: el filtro AssetTypeIds=8, MinPrice=0, MaxPrice=0,
Keyword=valkyrie devolvio 11 resultados, incluidos los tres IDs anteriores.
Esto verifica su descubrimiento, no que ya se hayan importado a la base local.


El plan tambien limita el numero total de particiones por ejecucion a
`CoverageMaxPartitions=512` (configurable entre 200 y 4096). Al alcanzar el
presupuesto, los padres siguen en PossiblyTruncated y se registra un aviso;
no se declara cobertura completa ni se generan subdivisiones ilimitadas.
