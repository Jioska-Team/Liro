# Series de ventas y RAP

Estas rutas leen los datos existentes; no consultan Roblox, no guardan puntos,
no reconstruyen ventas individuales y no modifican los workers.
El endpoint anterior `GET /api/v1/items/{id}/market-history` conserva su contrato.
`id` es el GUID interno de Items, obtenido, por ejemplo, de
`GET /api/v1/items/by-asset/215718515`.

## Ventas por dia

```http
GET /api/v1/items/{id}/market-history/daily-sales?from=2026-10-01&to=2026-10-20&limit=100
```

Ejemplo ilustrativo:

```json
{
  "fromUtc": "2026-10-01",
  "toUtcExclusive": "2026-10-20",
  "points": [
    {
      "dateUtc": "2026-10-01",
      "averagePriceRobux": 125,
      "salesVolume": 2,
      "updatedAtUtc": "2026-10-02T00:10:00Z"
    }
  ],
  "nextAfter": null
}
```

Fuente: MarketHistoryPoints, Source `marketplace-sales`, metricas
`PrecioMedioVenta` y `VolumenVentas`. Son agregados diarios ya calculados por la
fuente: no se suman observaciones repetidas, no se calcula el precio medio usando
RAP. Si hay varios periodos de una metrica dentro de un dia UTC, se usa el ultimo
periodo y su observacion mas reciente. Se entrega una sola fila por dia.
`updatedAtUtc` es la observacion mas reciente de las metricas presentes; no es la
hora de una compra ni garantiza que ambas metricas se hayan recibido juntas.

Un valor ausente es `null`; cero sigue siendo cero. Un dia sin ninguna metrica no
aparece: no se inventan ventas cero. El dia actual puede estar incompleto y
cambiar cuando mercado guarde otra observacion.

`dateUtc` representa el dia del agregado. Mostrarlo como fecha de calendario,
sin convertir medianoche UTC a hora local: esa conversion puede mostrar el dia
anterior en Guayaquil. Los timestamps de RAP si pueden visualizarse en hora local.

## Valores observados de RAP

```http
GET /api/v1/items/{id}/market-history/rap?from=2026-10-01&to=2026-10-20&limit=100
```

```json
{
  "fromUtc": "2026-10-01",
  "toUtcExclusive": "2026-10-20",
  "points": [
    { "observedAtUtc": "2026-10-01T14:03:00.123456Z", "rap": 3154 },
    { "observedAtUtc": "2026-10-01T14:19:00.654321Z", "rap": 3160 }
  ],
  "nextAfter": null
}
```

Fuente: MarketRapSnapshots. Cada punto es el RAP observado por el trabajador,
no el precio de una transaccion ni una notificacion instantanea de Roblox.
La ruta no genera puntos entre observaciones, ni un valor inicial anterior al
rango. El primer punto disponible puede ser la primera observacion guardada.

## Fechas, paginacion y limites

- `from` y `to`: fechas ISO `yyyy-MM-dd`, calendario UTC; inicio incluido y fin
  excluido. Por defecto: ultimos 30 dias UTC, incluido hoy.
- Rango positivo de hasta 366 dias. Para periodos mas antiguos consultar ventanas
  consecutivas sin solaparlas, por ejemplo de 2016-01-01 a 2017-01-01.
- `limit`: 1–500, por defecto 100. Orden ascendente, antiguo a reciente.
- Si `nextAfter` no es null, repetir la misma ruta, `from`, `to` y `limit`, pasando
  ese valor exacto en `after`. El cursor es exclusivo; no se usa OFFSET. En RAP
  conservar todos los decimales del timestamp y codificarlo como parametro URL.
- `nextAfter: null` significa que no quedan mas puntos en ese rango. Para repetir
  la consulta en otra ocasion y detectar revisiones diarias, volver a consultar
  el rango; la paginacion no es un registro inmutable de eventos.
- Articulo inexistente: 404. Articulo existente sin datos: 200 y `points: []`.
  Parametros invalidos: 400. Mas de dos consultas simultaneas a estas rutas por
  instancia de API: 429, sin cola; reintentar mas tarde. Este limite es de lectura
  local y es independiente del presupuesto de solicitudes a Roblox.

## Arquitectura y operacion

Controlador en Api, contrato de lectura en Application, consultas en Infrastructure.
Se reutilizan las claves de las tablas existentes; no hay migraciones ni nuevas
tablas. Se consultan solo las columnas necesarias, sin tracking ni SaveChanges.
El rango y el limite acotan las lecturas. El endpoint consulta existencia del
articulo y luego la serie; no realiza consultas por cada punto.
Los limites de concurrencia son por proceso: no constituyen un limitador
distribuido. Se acumulan con la [protección general de lecturas](api-route-conventions.md#proteccion-de-entrada-http), ya implementada.
No se reinician procesos al aplicar el codigo. Las rutas se cargan con la proxima
compilacion y puesta en marcha de la API.
