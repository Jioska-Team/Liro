# Convencion de rutas HTTP de Liro

Esta es la convencion para nuevas rutas. Las rutas existentes forman parte del
contrato y no se renombran por estetica. Es una decision del proyecto basada en
claridad y consistencia; no una regla literal del libro Clean Code.

## Vocabulario y estructura

- API de negocio: `/api/v1`, ingles, minusculas y `kebab-case` en segmentos
  compuestos. Colecciones en plural (`items`, `imports`, `bundles`); recursos
  singulares cuando expresan un concepto unico (`catalog`, `market-summary`).
- Los segmentos nombran recursos o relaciones. El metodo HTTP expresa la
  operacion: GET consulta y no modifica; POST crea o ejecuta una importacion.
  No introducir rutas como `/getItems`, `/saveItem` o una variante por cada tipo.
- `/catalog` representa la navegacion del catalogo oficial visible, aplicando las
  reglas de visibilidad. `/items` identifica articulos individuales guardados,
  incluidos assets y bundles. No son dos nombres para el mismo listado.
- `/{id}` bajo `/items` es el GUID interno. `assetId`, `bundleId` y `robloxId`
  son identificadores externos numericos positivos. El ID externo por si solo
  no distingue asset de bundle: usar siempre su tipo.
- Se conservan `/items/by-asset/{assetId}` y `/items/by-bundle/{bundleId}` como
  consultas explicitas por identidad externa. No agregar sinonimos equivalentes.
- Las relaciones usan jerarquias: `/catalog/bundles/{bundleId}/components` y
  `/catalog/assets/{assetId}/bundles`.
- El mercado de un articulo vive bajo `/items/{id}/market-summary` y
  `/items/{id}/market-history`. Las series usan `/daily-sales` y `/rap`.
  RAP es Recent Average Price; no es el precio de una venta individual.
- Filtros, fechas y paginacion van en query string. En nuevas series se prefiere
  cursor exclusivo; no se cambia la paginacion actual de `/catalog` en esta tarea.
- JSON y parametros: `camelCase`. Fechas ISO 8601; timestamps UTC. Las series
  diarias usan calendario UTC y fin exclusivo, como documenta market-history-api.md.
- Rutas operativas como `/api/lan/ready`, salud e infraestructura mantienen su
  contrato separado de la API de negocio; no se versionan solo por uniformidad.

## Importacion recomendada

```http
POST /api/v1/catalog/imports
Content-Type: application/json
X-Liro-Admin-Key: <secreto del entorno>
```

```json
{ "itemType": "Asset", "robloxId": 215718515 }
```

Para un paquete usar `"itemType": "Bundle"`. Ambos campos son obligatorios.
Ejemplos ejecutables: [coleccion administrativa de Postman](catalog-imports.postman_collection.json).
Esta coleccion se mantiene separada de las consultas publicas de frontend LAN.
`itemType` admite exactamente `Asset` o `Bundle`, no numeros ni otros valores.
`robloxId` es un entero positivo de 64 bits.

Esta operacion sincrona importa o actualiza un articulo mediante el servicio
existente. No crea una tabla de trabajos ni devuelve un identificador de tarea.
No se garantiza que cada POST evite una nueva consulta a Roblox: repetirlo puede
refrescar los datos. La identidad persistida sigue siendo tipo + ID externo.

| Resultado | Respuesta |
|---|---|
| Articulo importado o actualizado | 200 con el mismo ItemResponse de las rutas anteriores |
| Cuerpo, tipo o ID invalidos | 400; no se invoca el importador |
| Sin autenticacion administrativa valida | 401 |
| No se obtiene un articulo importable con las reglas actuales | 404 |
| Cupo local de escritura agotado | 429 |

Se exige HTTPS y la politica `CatalogAdmin`. La nueva ruta comparte la politica
`catalog-writes` de las anteriores: 10 solicitudes por minuto por instancia de
API, sin un cupo adicional por ruta. Este es el limitador de entrada a la API;
el presupuesto de peticiones salientes a Roblox se conserva sin cambios.
Las reglas actuales de pertenencia al catalogo y refresco no se modifican.

## Compatibilidad

| Ruta compatible | Equivalente recomendado |
|---|---|
| `POST /api/v1/items/import/{assetId}` | `POST /api/v1/catalog/imports` con `Asset` y el mismo ID |
| `POST /api/v1/items/import-bundle/{bundleId}` | `POST /api/v1/catalog/imports` con `Bundle` y el mismo ID |

Las rutas anteriores siguen ejecutando el mismo servicio una sola vez y conservan
autorizacion, limitador y respuestas. No redirigen, no requieren cuerpo y no se
retiran en esta version. Los clientes nuevos deben usar la ruta recomendada;
los clientes existentes pueden migrar sin urgencia. No hay fecha de retirada.

Cualquier retirada o cambio incompatible requiere revisar los consumidores y
publicar una migracion/version apropiada. Una ruta nueva no debe relajar la
seguridad ni duplicar logica de importacion en controladores.

## Lecturas del catalogo y cursor

La navegacion existente sigue disponible sin cambios de formato:
`GET /api/v1/catalog?page=1&pageSize=24` devuelve `items`, `page`, `pageSize`, `hasMore`.
Conserva el acceso por numero de pagina y su implementacion con OFFSET por compatibilidad.

Para recorridos nuevos usar `GET /api/v1/catalog/feed?pageSize=24`.
La respuesta es `{ "items": [...], "pageSize": 24, "hasMore": true, "nextCursor": "..." }`.
Continuar con `GET /api/v1/catalog/feed?pageSize=24&after=<nextCursor>` hasta que
`hasMore` sea false y `nextCursor` sea null. El tamano permitido es 1–100, por defecto 24.
No combinar `page` con esta ruta. Cursores vacios, malformados o de otra version
producen 400; no se ejecuta la consulta de almacenamiento.

El cursor es una posicion opaca versionada, no una credencial: transmitirlo sin
modificar y codificarlo como parametro URL. No contiene secretos ni caduca con un
reinicio. Orden: fecha de creacion Roblox conocida primero, fecha descendente,
ID interno ascendente como desempate; los articulos sin fecha quedan al final.
La fecha y el ID se incluyen en el cursor, por lo que borrar el articulo de anclaje
no impide continuar. La consulta usa keyset y LIMIT, sin OFFSET ni conteo total.
Conserva el filtro de creador oficial y oculta piezas corporales como la ruta antigua.
No es una instantanea transaccional: altas, cambios de visibilidad o de fecha entre
paginas pueden cambiar el conjunto. Para obtener los articulos mas nuevos, iniciar
un recorrido nuevo. No se promete acelerar todas las consultas sin medir sus planes.

### Proteccion de entrada HTTP

GET/HEAD bajo `/api/v1/catalog` y `/api/v1/items` comparten por instancia de API:
- Dos lecturas simultaneas en total, sin cola.
- 120 admisiones por ventana fija de un minuto en total.
- 60 admisiones por ventana fija de un minuto por IP remota.

Las rutas antiguas y nuevas comparten los mismos contadores. Una admision rechazada
por un limitador posterior puede consumir conservadoramente el token de frecuencia
anterior; no se garantiza que todas las cuotas disponibles sean utilizables.
Al llegar al limite: HTTP 429, Problem Details y Retry-After en segundos (un segundo
si se rechaza por concurrencia). Los clientes deben respetarlo y evitar reintentos
simultaneos. Estos son limites por ventana fija, no por cualquier ventana movil de
60 segundos; los limites de concurrencia permanecen activos al cambiar la ventana.

La identidad usa RemoteIpAddress; no confia directamente en X-Forwarded-For.
Usuarios tras la misma NAT/proxy comparten cuota. Antes de distribuir varias replicas,
revisar el presupuesto conjunto y configurar proxies confiables si corresponde.
`/api/lan/ready`, `/health`, `/alive` y los metodos de escritura quedan fuera de estos
contadores. Las escrituras conservan autenticacion HTTPS/clave y su limite anterior.
Las series historicas conservan ademas su politica existente de concurrencia.
No afecta a las peticiones salientes de los workers ni al presupuesto compartido de
Roblox (18 globales, una por minuto para mercado). No se agregan migraciones, indices,
tablas ni depuraciones de datos.
