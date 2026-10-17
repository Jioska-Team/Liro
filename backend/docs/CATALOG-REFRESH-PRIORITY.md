# Actualizaciones prioritarias del catálogo

El descubrimiento histórico sigue incorporando artículos. El refresco confirma
los cambios de artículos existentes. Ambos conservan los filtros de creador
oficial (User, ID 1) y la identidad compuesta Asset/Bundle + ID.

## Flujo prioritario

`CatalogLimitedMonitor` recorre todas las páginas que Roblox entregue, sin una
espera de un minuto entre páginas. Siempre consulta primero la primera página,
retoma el cursor guardado y continúa hasta que no haya siguiente cursor. Guarda
progreso después de cada página aplicada. Durante recorridos largos vuelve a
consultar la primera página aproximadamente cada minuto, sin reemplazar el
cursor del recorrido. Al terminar la vuelta espera `LimitedPollInterval`
(un minuto) antes de empezar otra. Se conservan la regulación HTTP global,
los turnos para otros trabajos, las esperas por errores y `Retry-After`.
No espera a importar las páginas históricas. Solo considera señales explícitas `Limited` o
`LimitedUnique` de la cuenta oficial.

Los detalles completos de cada página se aplican directamente sin volver a
consultar `/v1/catalog/items/details`. Se usa el mismo validador que para ese
endpoint, comprobando identidad Asset/Bundle y creador oficial. La búsqueda
reciente compara también artículos existentes; la carga histórica reutiliza
los detalles para incorporar los nuevos. Una respuesta incompleta o duplicada
requiere la consulta de respaldo. Las observaciones llevan el origen
`RobloxCatalogSearchV2`.

Las señales incompletas se guardan en `CatalogPriorityRefreshes`, incluso para artículos
conocidos que aún no son limiteds. Una clave compuesta impide duplicados y
conserva la primera hora de detección. Los artículos que ya son limiteds en
la base no se vuelven a encolar. Los candidatos desconocidos también se
confirman e incorporan, incluyendo bundles.

El refresco atiende un lote prioritario de respaldo y luego un lote normal. Confirma los
detalles con Roblox antes de actualizar el estado y emitir el evento existente.
La entrada solo se elimina después de guardar correctamente. Un reinicio no
pierde la cola; si ocurrió entre guardado y confirmación de la cola, puede
repetirse una consulta. Los errores conservan la entrada y respetan el backoff
y la cuarentena de `CatalogImportFailures`. Un 429 conserva el trabajo sin
consumir intentos individuales.

La salida HTTP continúa siendo serial y conserva la pausa global de tres
segundos y `Retry-After`. Las solicitudes urgentes pasan antes que las normales,
pero cuando ambas esperan se concede al menos un turno normal por cada cuatro
concesiones. Una solicitud ya iniciada no se interrumpe. La coordinación es por
proceso: no coordina instancias adicionales de workers ni el proceso API.

Una página sin cambios no vuelve a guardar Items ni añade snapshots de mercado.
Tampoco adelanta `LastCheckedAt` o `NextRefreshAt`: la revisión por identificador
y la renovación de imágenes siguen teniendo su turno. Si una búsqueda comenzó
antes de una revisión ya completada, se ignora esa observación para evitar
reemplazar datos más recientes. Los conflictos concurrentes conservan el
mecanismo de reintento existente.

La aplicación directa de páginas omite las miniaturas; también para artículos
nuevos, la imagen se completará en el refresco normal. La mejora elimina las
consultas redundantes por detalle; no elimina la paginación ni garantiza que
Roblox exponga todos los artículos en una búsqueda.

## Refresco normal e imágenes

Las páginas de búsqueda se guardan por lotes de hasta 120 identidades tipadas.
El camino normal realiza dos consultas de lectura: artículos con su estado de
mercado y reintentos de esas identidades. Las comprobaciones y comparaciones
posteriores se realizan en memoria reutilizando el importador y sus validaciones.
Un único `SaveChanges` agrupa artículos nuevos/modificados, estados de mercado,
snapshots, eventos de outbox y eliminación de reintentos resueltos, mediante la
transacción implícita de EF Core. Esto reduce viajes a PostgreSQL, pero no
equivale necesariamente a una sola sentencia SQL: el proveedor divide los
comandos en sus propios lotes. Si no hay cambios ni reintentos que resolver,
no se invoca el guardado.

Las filas inválidas o que requieren eliminación se aíslan para el camino
individual. Ante conflictos de concurrencia o restricciones, se descarta el
contexto del lote fallido y se reprocesan las observaciones en contextos nuevos,
sin volver a descargar los detalles válidos. Los fallos de infraestructura no
se convierten en 120 reintentos individuales: se propagan y conservan el cursor.
El checkpoint sigue avanzando solo después de guardar la página o registrar sus
fallos. Se mantiene el bloqueo por backoff/cuarentena y la separación Asset/Bundle.

`CatalogWorkers:PageDelay` ahora vale cero: se elimina la pausa adicional de cinco
segundos de la carga histórica y de las páginas recientes. Continúan vigentes el
intervalo global de solicitudes a Roblox, las pausas `429` y los ciclos de sondeo.
Esta optimización no introduce una migración de base de datos.

Mientras haya trabajo, no existe la antigua pausa de cinco minutos entre grupos.
Cada vuelta atiende hasta `DetailsBatchSize` prioritarios y un lote normal del
mismo tamaño, con preferencia parcial para reintentos. Cuando no hay trabajo
elegible espera `RefreshIdleDelay` (15 segundos). Los fallos generales esperan
`RetryDelay`. Se conserva `ItemDelay` por lote normal y la regulación HTTP.

`RefreshInterval` (30 minutos) sigue indicando elegibilidad, no una garantía de
latencia. `BatchSize` permanece por compatibilidad con configuración existente;
el tamaño del turno de refresco ahora usa `DetailsBatchSize` (10).

Las miniaturas se consultan al incorporar, cuando faltan o tras
`Catalog:ThumbnailRefreshInterval` (24 horas). `ThumbnailCheckedAtUtc` registra
el intento para no repetir solicitudes si la URL no cambia. `ThumbnailUpdatedAt`
mantiene su significado de cambio de URL. Si no hay imagen, el próximo intento
es elegible tras 15 minutos, atendido por el refresco normal. Un intento fallido
con imagen existente conserva la imagen y espera su siguiente renovación.
Los lotes prioritarios no consultan imágenes; su renovación corresponde al
refresco normal, para no demorar la confirmación de limiteds.

## Activación y observación

Reiniciar AppHost/workers con el código compilado. En Development el inicializador
aplica `AddCatalogPriorityRefresh`: crea la cola y añade una columna nullable en
Items; no reinicia el catálogo ni sus checkpoints. En producción aplicar primero
las migraciones mediante el procedimiento habitual.

Configuración adicional de workers:

```json
{
  "Catalog": { "ThumbnailRefreshInterval": "1.00:00:00" },
  "CatalogWorkers": {
    "LimitedMonitorEnabled": true,
    "LimitedPollInterval": "00:01:00",
    "RefreshIdleDelay": "00:00:15"
  }
}
```

Registros:

- `Catalog search page downloaded`: filas y `DownloadMs` (solicitud, lectura y
  deserialización de la página; incluye esperas del regulador HTTP).
- `Catalog database batch saved`: candidatos, inserciones, actualizaciones,
  registros que necesitan respaldo individual, `ReadMs`, `PrepareMs` y `SaveMs`.
  Estas medidas separan descarga, lecturas SQL, comparación y guardado; no se
  ha establecido una aceleración numérica garantizada.
- `Limited monitor polled`: sondeo completado y candidatos de primera página.
- `Limited scan completed`: páginas consultadas, observaciones recibidas y
  segundos de la vuelta. Las consultas adicionales a la primera página cuentan
  también; las observaciones no equivalen a artículos únicos.
- `Catalog search page applied`: detalles reutilizados, candidatos de respaldo,
  incorporaciones y fallos. `ReusedDetails` cuenta observaciones procesadas,
  no necesariamente escrituras, porque las observaciones sin cambios se omiten.
- `Priority catalog item confirmed`: artículo confirmado y segundos desde
  detección hasta guardado (`DetectionToSaveSeconds`). No mide desde el cambio
  original en Roblox, cuya hora no conocemos.
- `Catalog queue health`: pendientes prioritarios, detección más antigua,
  artículos vencidos y vencimiento más antiguo. Se emite aproximadamente cada
  minuto al terminar una vuelta; incluye bloqueados por errores/cuarentena.

```sql
SELECT COUNT(*) AS "Prioritarios",
       MIN("DetectedAtUtc") AS "DeteccionMasAntigua",
       CURRENT_TIMESTAMP - MIN("DetectedAtUtc") AS "EsperaMasAntigua"
FROM "CatalogPriorityRefreshes";

SELECT COUNT(*) AS "Vencidos", MIN("NextRefreshAt") AS "VencimientoMasAntiguo"
FROM "Items"
WHERE "NextRefreshAt" <= CURRENT_TIMESTAMP;

SELECT q."ItemType", q."ItemId", q."DetectedAtUtc",
       f."NextAttemptAtUtc", f."DeadLetteredAtUtc", f."Error"
FROM "CatalogPriorityRefreshes" q
LEFT JOIN "CatalogImportFailures" f
  ON f."ItemType" = q."ItemType" AND f."AssetId" = q."ItemId"
ORDER BY q."DetectedAtUtc";
```

El recorrido ya no añade un minuto por página pendiente, pero no existe un SLA.
La publicación/indexación en Roblox, las
páginas pendientes, el volumen de candidatos, la latencia y los 429 pueden
alargarlo. La búsqueda reciente no garantiza exponer toda conversión a limited;
el recorrido y refresco general siguen siendo la cobertura complementaria.
