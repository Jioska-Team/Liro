# API de Liro para frontend

Guía actualizada contra controllers y política LAN el 21 de octubre de 2026.
Esta guia describe lo implementado, no endpoints futuros. Los modelos auxiliares están en
[api-models.ts](api-models.ts) y hay solicitudes importables en
[liro-frontend.postman_collection.json](liro-frontend.postman_collection.json).
Estos artefactos no garantizan cobertura de todas las rutas posteriores; no se modificaron
TypeScript ni JSON en esta auditoría documental. Para listado y series, consultar las guías enlazadas.

## Conexion sin instalar el backend

1. Pulsar **Iniciar / obtener API** en Discord con el rol autorizado.
2. Copiar la URL base devuelta a la configuracion local del frontend o a la variable
   `baseUrl` de Postman. Sin `/api` al final y sin barra final en los ejemplos de esta guia.
3. Abrir `{{baseUrl}}/api/lan/ready`. Un 200 con `status: "ready"` confirma disponibilidad.
4. Abrir Angular en `http://localhost:4200`. Ese es el origen CORS configurado;
   `http://127.0.0.1:4200`, HTTPS y otros puertos son origenes distintos.

La URL depende de la red del anfitrion. No guardar su IP, tokens o claves en Git.
No se necesita Docker en la PC del frontend. La PC anfitriona debe seguir encendida
y ambos equipos deben tener conectividad en la misma subred permitida.
No enviar cookies, `Authorization` ni claves administrativas: la API LAN es de lectura.
La raiz `/` devuelve 403 por diseno; no es una web ni un Swagger UI.

## Endpoints disponibles por LAN

Todas estas solicitudes son GET, sin cuerpo ni autenticacion.

| Ruta | Uso | Respuesta 200 | Errores particulares |
| --- | --- | --- | --- |
| `/api/lan/ready` | Comprobar API, PostgreSQL y cache | `LanReadinessResponse` | 503 si no esta lista |
| `/api/v1/items/by-asset/{assetId}` | Buscar un item guardado por ID numerico Roblox | `ItemResponse` | 400 si ID <= 0; 404 si no existe |
| `/api/v1/items/by-bundle/{bundleId}` | Buscar un bundle oficial guardado | `ItemResponse` | 400 si ID <= 0; 404 si no existe |
| `/api/v1/items/{id}` | Buscar por UUID interno de Liro | `ItemResponse` | 404 si no existe |
| `/api/v1/items/{id}/market-history?limit=100&before=...` | Observaciones historicas de mercado | `ItemMarketResponse[]` | 400 parametros invalidos; 404 si no existe el item |

`assetId` y `id` NO son intercambiables. Primero consulta por `assetId` y usa el `id`
UUID devuelto para solicitar el historial. Un ID que no cumple la restriccion de ruta
(por ejemplo texto donde se requiere UUID) puede dar 404 en lugar de 400.
Consultar un item no lo importa ni fuerza una actualizacion desde Roblox.
Un 404 puede significar que existe en Roblox pero aun no esta en Liro.

No existe GET `/api/v1/items`, pero la grilla ya dispone de
`GET /api/v1/catalog?page=1&pageSize=24` y de `GET /api/v1/catalog/feed?pageSize=24`.
Ver [contrato de listado y cursor](../api-route-conventions.md#lecturas-del-catalogo-y-cursor).
No hay filtros públicos por nombre/creador ni endpoints de favoritos, vendedores o propietarios.

También se permiten por LAN estas lecturas bajo las mismas reglas de subred y origen:

| Ruta GET | Contrato |
| --- | --- |
| `/api/v1/catalog/bundles/{bundleId}/components` | Bundle, componentes y IDs pendientes; [relaciones](../catalogo-paquetes-componentes.md) |
| `/api/v1/catalog/assets/{assetId}/bundles` | Bundles padres del asset |
| `/api/v1/items/{id}/market-summary` | Resumen de seguimiento; distinto de `ItemResponse.market` |
| `/api/v1/items/{id}/market-history/daily-sales` | [Serie diaria de ventas](../market-history-api.md) |
| `/api/v1/items/{id}/market-history/rap` | [Serie de RAP observado](../market-history-api.md) |

Los detalles de respuesta de market-summary están en
[su contrato](../../src/Liro.Application/Catalog/Repositories/IItemMarketSummaryRepository.cs).
Las consultas no importan ni fuerzan actualizaciones de Roblox.

### Historial y paginacion

- `limit`: entero de 1 a 200, predeterminado 100.
- `before`: fecha ISO 8601 con zona, recomendable UTC, por ejemplo
  `2026-10-15T12:00:00Z`. Si falta, se usa la hora UTC actual del servidor.
- Orden descendente por `observedAtUtc`, filtro estricto `observedAtUtc < before`.
- Para otra pagina, usar como `before` el `observedAtUtc` del ultimo elemento.
- Devuelve un array directo, sin `total`, `nextCursor` ni sobre `data` externo.
  `[]` es una respuesta valida para un item sin observaciones anteriores.
- Son observaciones almacenadas por Liro, no transacciones individuales ni toda la
  historia de Roblox. No hay garantia de periodicidad exacta. Si se necesitan
  graficas ascendentes, invertir una copia del resultado en el frontend.
- El cursor solo contiene fecha; no hay desempate publicado para observaciones con
  la misma marca temporal. No asumir paginacion sin perdidas si hubiera empates.

## Modelos y reglas de presentacion

JSON usa `camelCase`. Las propiedades anulables se reciben como `null`: no equivalen
a cero, falso o cadena vacia. `restrictions` puede ser `[]` y `market` puede ser `null`.
Los tipos TypeScript adjuntos describen JSON; no validan los datos en tiempo de ejecucion.

| Campo de ItemResponse | Significado |
| --- | --- |
| `id` | UUID interno, usado para detalle e historial |
| `robloxItemId`, `itemType` | Identidad Roblox: ID numerico y tipo `Asset` o `Bundle`; dos tipos pueden compartir ID |
| `robloxAssetId` | ID para assets; null para bundles. Para codigo nuevo usar robloxItemId + itemType |
| `bundleTypeId` | Codigo numerico del tipo de bundle; null para assets. No confundir con assetTypeId |
| `collectibleItemId` | UUID collectible cuando esta disponible; su presencia no sustituye `marketStatus` |
| `name`, `description` | Nombre y descripcion del catalogo; mostrar descripcion como texto, no HTML confiable |
| `marketplaceUrl` | Enlace externo al marketplace; usar este valor en lugar de construir un slug |
| `thumbnailUrl`, `thumbnailUpdatedAt` | Imagen y fecha local de actualizacion de imagen; usar placeholder si es null |
| `creatorId`, `creatorType`, `creatorName` | Identidad del creador, tipo y nombre observados; pueden faltar en datos antiguos |
| `assetTypeId` | Codigo numerico del tipo de asset de Roblox; no es un ID de creador ni un estado de venta |
| `marketStatus` | Enum numerico: 0 Unknown, 1 Normal, 2 Limited, 3 LimitedUnique, 4 Collectible |
| `restrictions` | Etiquetas del proveedor, por ejemplo Limited o LimitedUnique; aceptar etiquetas desconocidas |
| `firstSeenAt` | Primera incorporacion a Liro |
| `lastUpdatedAt` | Actualizacion local de datos del item; no es una fecha oficial de modificacion de Roblox |
| `lastCheckedAt` | Ultima comprobacion registrada; puede ser null |
| `robloxCreatedAtUtc` | Primer `itemCreatedUtc` conservado por Liro |
| `robloxUpdatedAtUtc` | Se inicializa con la fecha anterior y avanza si un `itemCreatedUtc` posterior es mayor; inferencia del proyecto, no un campo oficial de modificacion |
| `market` | Ultima observacion de mercado almacenada, si existe |

Las fechas son cadenas ISO 8601, normalmente UTC; convertirlas al huso del usuario
para mostrarlas. Los IDs y contadores `long` se serializan actualmente como numeros JSON.
JavaScript solo representa exactamente enteros hasta `Number.MAX_SAFE_INTEGER`.
Comprobar `Number.isSafeInteger` donde corresponda; un valor mayor requeriria cambiar
el contrato o usar un parser JSON que preserve enteros, no convertir a string despues
de perder precision. No usar separadores de miles en IDs enviados en URLs.

### Mercado: precios y disponibilidad

`ItemMarketResponse` contiene `data`, `observedAtUtc`, `source`, `availability` y
`availablePriceRobux`. La fecha es la de observacion y `source` identifica su procedencia.

| Campo dentro de `data` | Significado |
| --- | --- |
| `primaryPriceRobux` | Precio de venta primaria observado; no garantiza precio historico de lanzamiento |
| `priceBeforeDiscountRobux` | Precio anterior al descuento reportado |
| `lowestPriceRobux` | Menor precio reportado por el proveedor |
| `lowestResalePriceRobux` | Menor precio de reventa reportado; no es el precio de la ultima venta |
| `isOffSale` | Estado de venta primaria; puede ser true y existir reventa |
| `hasResellers` | Indica presencia de revendedores; no contiene su lista ni cantidad |
| `isResellable` | Indica si admite reventa segun los datos disponibles |
| `priceStatus` | Texto de estado del proveedor, sin enum cerrado |
| `totalQuantity` | Cantidad total reportada; no equivale necesariamente a copias vendidas |
| `unitsAvailableForConsumption` | Unidades disponibles reportadas; no es recuento de vendedores |
| `favoriteCount` | Total de favoritos del item; no indica si el usuario actual lo marco favorito |

Para el precio visible usar `market.availablePriceRobux`: el backend elige el menor
entre el primario (si `isOffSale == false`) y el de reventa (si `hasResellers == true`)
cuando ambos existen, o el unico disponible. Cero es un precio valido; no usar
`precio || 'Sin precio'`, sino comprobar `precio == null`.

Usar `market.availability` para la etiqueta:

| Valor JSON | Etiqueta sugerida |
| --- | --- |
| `OnSale` | En venta |
| `OffSale` | Off Sale |
| `NoSellers` | No Sellers |
| `Unknown` | Sin informacion suficiente |

No deducir disponibilidad solo de Limited, `isOffSale` o de un precio null.
Este modelo de observación de catálogo no incluye RAP, promedio diario de ventas,
última transacción, demanda, valor estimado ni propietarios. RAP y ventas se consultan
mediante las rutas de series independientes enlazadas arriba.

## Ejemplos

En PowerShell (sustituir la URL localmente; no publicar la IP en el repositorio):

```powershell
$baseUrl = Read-Host 'URL base entregada por Discord, sin barra final'
Invoke-RestMethod "$baseUrl/api/lan/ready"
$item = Invoke-RestMethod "$baseUrl/api/v1/items/by-asset/12803855954"
Invoke-RestMethod "$baseUrl/api/v1/items/$($item.id)/market-history?limit=20"
```

El ID del ejemplo puede no estar importado en todas las instalaciones.
Respuesta de disponibilidad:

```json
{"service":"liro-api","status":"ready"}
```

Ejemplo **ficticio**, no datos reales de Roblox, de mercado:

```json
{
  "data": {
    "primaryPriceRobux": null,
    "priceBeforeDiscountRobux": null,
    "lowestPriceRobux": 120,
    "lowestResalePriceRobux": 120,
    "isOffSale": true,
    "hasResellers": true,
    "isResellable": true,
    "priceStatus": null,
    "totalQuantity": 1000,
    "unitsAvailableForConsumption": 0,
    "favoriteCount": 50
  },
  "observedAtUtc": "2026-10-15T12:00:00Z",
  "source": "example",
  "availability": "OnSale",
  "availablePriceRobux": 120
}
```

Para Angular, consumir los modelos adjuntos con `HttpClient.get<ItemResponse>(url)`
y `HttpClient.get<ItemMarketResponse[]>(url)` para historial. Configurar la base URL
en configuracion local; no usar `withCredentials: true` ni interceptores que adjunten
tokens a estas llamadas. No se requiere un SDK especifico de Liro.

Las lecturas comparten cuotas por proceso e IP, además de la concurrencia de series;
ver [límites HTTP](../api-route-conventions.md#proteccion-de-entrada-http).

## Errores

| HTTP | Interpretacion / accion |
| --- | --- |
| 400 | Parametro, fecha o modelo invalido; revisar `errors` si existe |
| 401 | Credencial administrativa ausente o invalida; las consultas LAN no requieren clave |
| 403 | Ruta/metodo no permitido o equipo fuera de subred; abrir `/` tambien produce 403 |
| 404 | Item o ruta inexistente; no reintentar indefinidamente |
| 409 | Conflicto de escritura o item duplicado; administrativo |
| 429 | Cuota de lecturas, concurrencia de series o escrituras alcanzada; respetar Retry-After y evitar reintentos simultáneos |
| 500 | Error no previsto; reportar `traceId` si existe |
| 502 | Proveedor externo no disponible o respuesta invalida |
| 503 | Dependencias no listas; en limitacion de Roblox puede incluir `Retry-After` |

El manejador de excepciones devuelve Problem Details con `status`, `title` y `traceId`.
Validacion MVC puede agregar `errors`. No asumir JSON en todos los errores: el
middleware LAN devuelve 403 sin cuerpo. El 503 de readiness usa
`{"service":"liro-api","status":"unavailable"}`, no Problem Details.
En navegador, un fallo CORS o de conectividad puede aparecer como status 0 en Angular;
no equivale a un 403 HTTP recibido. Postman no verifica CORS.

## Otras rutas existentes, fuera del frontend LAN

| Metodo/ruta | Uso / contrato |
| --- | --- |
| POST `/api/v1/catalog/imports` | Ruta recomendada de importacion; body `{ "itemType": "Asset", "robloxId": 215718515 }` o tipo `Bundle`; 200 ItemResponse o 404 |
| POST `/api/v1/items/import/{assetId}` | Ruta compatible de importacion de assets; sin body; 200 ItemResponse o 404 |
| POST `/api/v1/items` | Creacion administrativa; CreateItemRequest; 201 ItemResponse con Location, o 409 duplicado |
| GET `/api/infrastructure` | Diagnostico local Development: postgres, valkey, valkeyLatencyMs; 200/503; 404 fuera de Development |
| GET `/openapi/v1.json` | OpenAPI generado en Development; accesible por el endpoint local de administracion, bloqueado en LAN |

POST requiere HTTPS y `X-Liro-Admin-Key`, con 10 escrituras por minuto por proceso
(limitador compartido); LAN bloquea POST antes de autenticar. Nunca incluir esa clave
en Angular. CreateItemRequest exige assetId positivo, nombre de 1 a 200 caracteres,
collectibleItemId UUID o null y marketStatus numerico valido (0..4).
El importador actualmente acepta solo el creador oficial segun la politica del backend.

La nueva ruta conserva HTTPS, `CatalogAdmin` y el mismo limite `catalog-writes`
de las rutas compatibles. `itemType` debe ser `Asset` o `Bundle` y `robloxId`
un entero positivo. No hay retirada programada de las rutas anteriores.
Ver [convencion de rutas](../api-route-conventions.md) para identidad, estados y migracion.
Las rutas operativas `/health` y `/alive` tampoco se exponen por LAN.
No hay Swagger UI instalado; no usar `/swagger` como enlace de documentacion.

## Mantenimiento del contrato

Al modificar controllers, ItemResponse, ItemMarketData o la politica LAN, actualizar
esta guia, api-models.ts y las solicitudes de Postman en el mismo cambio.
El OpenAPI actual se genera desde handlers que devuelven IActionResult: puede omitir
esquemas de respuesta sin metadatos explicitos. Estos modelos revisados cubren ese hueco;
no se presenta un cliente generado como contrato completo.

## Bundles

Consultar `/api/v1/items/by-bundle/126` sigue el mismo contrato de detalle e historial.
`marketplaceUrl` usa `/bundles/{id}`; para assets usa `/catalog/{id}`.
`assetTypeId` es null en bundles; `bundleTypeId` identifica su categoria.
La API devuelve el bundle como objeto de catalogo, no enumera sus piezas internas.
La importacion administrativa `POST /api/v1/items/import-bundle/{bundleId}` exige
la misma autenticacion, HTTPS y limites que `import/{assetId}`; no esta habilitada
por LAN. Las consultas GET solo leen lo ya importado.
