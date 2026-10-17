# Descubrimiento adicional del catalogo

Las paginas de bundles oficiales aportan `bundledItems`. Solo se aceptan como
candidatos los elementos `type: Asset` con ID entero positivo. Se ignoran outfits,
elementos malformados y bundles de otros creadores. Cada candidato pasa por la
consulta de detalles existente: se verifica su creador User 1 antes de importar.
No se hereda el creador ni el estado limited del bundle. Se incluyen fuera de venta.

Los candidatos se deduplican por ItemType e ID; los ya almacenados se omiten.
Los repetidos en la misma pagina como resultados principales no se consultan otra vez.
Los detalles se solicitan por lotes, usando los limites y reintentos existentes.
El guardado de estos candidatos reutiliza el importador individual existente;
las observaciones completas de busqueda conservan su guardado agrupado.
El indice unico de Items(ItemType, RobloxAssetId) sigue protegiendo la base de datos.

El monitor prioritario de limiteds no consulta estos componentes: lo hacen los
recorridos de descubrimiento para no retrasar las comprobaciones de limiteds.
El contador de resultados para detectar truncamiento cuenta solo filas de busqueda,
no componentes. El log `Bundle components processed` informa candidatos, altas y errores.

Con `Roblox:Catalog:CoverageAdditionalFiltersEnabled=true` (predeterminado),
la cobertura historica incorpora ventas (SortType 2), precio ascendente (4) y
descendente (5), categorias 1/2/5/7 y tipos de venta Premium (3) y TimedOptions (4).
Los ordenamientos tambien complementan particiones saturadas. Cada combinacion
tiene su propio cursor. Todos envian IncludeNotForSale=true; las nuevas consultas
raiz no imponen limites de precio. Se conservan los limites globales de peticiones,
profundidad y particiones. No se prueban combinaciones arbitrarias de Topics ni
SortAggregation como si fueran filtros de fecha de creacion.

Las particiones de bundles llevan el sufijo `components-v1`: se recorren nuevamente
una vez tras actualizar, incluso si estaban completas, para descubrir sus componentes.
Los checkpoints anteriores de assets se conservan. Las nuevas variantes crean
checkpoints independientes sin borrar registros ni cursores existentes.

Reiniciar el backend para activar. Revisar el log `Coverage partition` y los campos
ItemsSeen, ItemsAdded, ItemFailures y CoverageStatus de CatalogCheckpoints para medir
el aporte de cada variante. Un recorrido SearchExhausted no demuestra que Roblox
haya expuesto todos sus articulos; PossiblyTruncated sigue indicando cobertura pendiente.
Los filtros pueden solaparse y no garantizan nuevas altas en cada consulta.

Ejecutar `scripts/Check-CatalogDuplicates.sql` sobre PostgreSQL para comprobar
duplicados. Solo consulta datos; no requiere migracion ni elimina filas.
