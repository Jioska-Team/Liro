# API de Liro para frontend

Contrato revisado contra el codigo del backend el 15 de octubre de 2026.
Esta guia describe lo implementado, no endpoints futuros. Los modelos estan en
[api-models.ts](api-models.ts) y las solicitudes importables en
[liro-frontend.postman_collection.json](liro-frontend.postman_collection.json).

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
| `/api/v1/items/{id}` | Buscar por UUID interno de Liro | `ItemResponse` | 404 si no existe |
| `/api/v1/items/{id}/market-history?limit=100&before=...` | Observaciones historicas de mercado | `ItemMarketResponse[]` | 400 parametros invalidos; 404 si no existe el item |

`assetId` y `id` NO son intercambiables. Primero consulta por `assetId` y usa el `id`
UUID devuelto para solicitar el historial. Un ID que no cumple la restriccion de ruta
(por ejemplo texto donde se requiere UUID) puede dar 404 en lugar de 400.
Consultar un item no lo importa ni fuerza una actualizacion desde Roblox.
Un 404 puede significar que existe en Roblox pero aun no esta en Liro.

**No existe GET `/api/v1/items` para listar todos los items**, ni busqueda por nombre,
filtros por creador, favoritos de usuarios o endpoints de vendedores/propietarios.
Un frontend que necesite una grilla del catalogo requiere acordar e implementar
ese contrato. No intentar suplirlo enumerando IDs de Roblox.

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
| `robloxAssetId` | ID numerico Roblox guardado |
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
El modelo no incluye RAP, promedio diario de ventas, ultima transaccion, demanda,
valor estimado ni propietarios. El historial registra estos mismos campos de mercado.

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

## Errores

| HTTP | Interpretacion / accion |
| --- | --- |
| 400 | Parametro, fecha o modelo invalido; revisar `errors` si existe |
| 401 | Credencial administrativa ausente o invalida; las consultas LAN no requieren clave |
| 403 | Ruta/metodo no permitido o equipo fuera de subred; abrir `/` tambien produce 403 |
| 404 | Item o ruta inexistente; no reintentar indefinidamente |
| 409 | Conflicto de escritura o item duplicado; administrativo |
| 429 | Limite de escrituras administrativas alcanzado |
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
| POST `/api/v1/items/import/{assetId}` | Importacion administrativa desde Roblox; sin body; 200 ItemResponse o 404 |
| POST `/api/v1/items` | Creacion administrativa; CreateItemRequest; 201 ItemResponse con Location, o 409 duplicado |
| GET `/api/infrastructure` | Diagnostico local Development: postgres, valkey, valkeyLatencyMs; 200/503; 404 fuera de Development |
| GET `/openapi/v1.json` | OpenAPI generado en Development; accesible por el endpoint local de administracion, bloqueado en LAN |

POST requiere HTTPS y `X-Liro-Admin-Key`, con 10 escrituras por minuto por proceso
(limitador compartido); LAN bloquea POST antes de autenticar. Nunca incluir esa clave
en Angular. CreateItemRequest exige assetId positivo, nombre de 1 a 200 caracteres,
collectibleItemId UUID o null y marketStatus numerico valido (0..4).
El importador actualmente acepta solo el creador oficial segun la politica del backend.
Las rutas operativas `/health` y `/alive` tampoco se exponen por LAN.
No hay Swagger UI instalado; no usar `/swagger` como enlace de documentacion.

## Mantenimiento del contrato

Al modificar controllers, ItemResponse, ItemMarketData o la politica LAN, actualizar
esta guia, api-models.ts y las solicitudes de Postman en el mismo cambio.
El OpenAPI actual se genera desde handlers que devuelven IActionResult: puede omitir
esquemas de respuesta sin metadatos explicitos. Estos modelos revisados cubren ese hueco;
no se presenta un cliente generado como contrato completo.
