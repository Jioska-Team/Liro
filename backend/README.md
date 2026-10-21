# Desarrollo y operación del backend Liro

## Organización de la solución

El host del catálogo se llama `Liro.CatalogWorker` (antes `Liro.Workers`). La solución agrupa Backend, Workers, Discord y Hosting. Ver [estructura, arquitectura y compatibilidad](docs/SOLUTION-STRUCTURE.md).

[Índice de documentación técnica](docs/INDEX.md).

## Documentacion para frontend

Guia de endpoints, respuestas, modelos TypeScript y coleccion de Postman:
[API para frontend](docs/frontend/README.md). No requiere Docker en el equipo del consumidor.

[Convencion de rutas e importaciones compatibles](docs/api-route-conventions.md).

## Verificación

La entrada vigente es [scripts/verification/Verify.ps1](scripts/verification/Verify.ps1).
Comprueba formato, compila en una copia temporal aislada y ejecuta las cuatro suites de
regresión de la solución, además de comprobaciones PowerShell. Son ejecutables propios;
un resultado vacío de `dotnet test` no acredita su ejecución.

La verificación completa exige `LIRO_TEST_POSTGRES` (base separada `liro_test_`) y
`LIRO_TEST_REDIS` (base lógica distinta de 0). No iniciarla sin esos recursos preparados.
Consultar [scripts operativos](scripts/README.md). Para cambios exclusivamente documentales,
validar enlaces, rutas y diferencias de archivos sin iniciar servicios.

## Arranque y migraciones

Se requiere .NET 10 SDK y Docker para la infraestructura local. Desde `backend/`,
ejecutar `dotnet run --project src/Liro.AppHost` o seleccionar Liro.AppHost en Visual Studio.
El recurso `workers` corresponde a Liro.CatalogWorker; `market-worker` depende de
`MarketWorker:Launch`. Para acceso remoto de desarrollo ver [LAN](LAN-DEVELOPMENT.md)
y [control Discord](DISCORD-DEVELOPMENT.md). API y Workers registran primero
DatabaseInitializationService. En Development aplica las migraciones antes de iniciar los procesos
que consumen tablas; EF/Npgsql coordina sus migraciones mediante su bloqueo de migración.
En otros entornos exige que todas las migraciones estén aplicadas y falla explícitamente si faltan.

Se conserva el historial de migraciones anterior. CatalogReliability añade seguimiento de
comprobaciones, índices, control de concurrencia xmin, reintentos y checkpoints. No elimina items.
Los items antiguos se programan para comprobarse; los eventos anteriores siguen siendo legibles.

Para herramientas EF, configurar `ConnectionStrings__lirodb` con la conexión exacta de la instancia
que se desea administrar. No se asume localhost:5432 ni una contraseña por defecto.
El valor no debe guardarse en Git ni pegarse en logs. Aspire entrega la conexión a los procesos,
pero una terminal separada no la recibe automáticamente.

Generar un script revisable:
`dotnet ef migrations script --idempotent --project src/Liro.Infrastructure --startup-project src/Liro.Api`

Aplicar durante una ventana controlada, con la conexión configurada:
`dotnet ef database update --project src/Liro.Infrastructure --startup-project src/Liro.Api`

Un fallo inicial consultando __EFMigrationsHistory puede preceder a la creación del historial en una
base nueva. Revisar el resultado final de migraciones, no solo una línea del log.
No borrar tablas ni volúmenes para resolver ese mensaje.

## Escrituras administrativas

GET /api/v1/items/{id} y GET /api/v1/items/by-asset/{assetId} siguen siendo públicos.
POST /api/v1/items y POST /api/v1/items/import/{assetId} requieren HTTPS y cabecera
`X-Liro-Admin-Key`. Hay un límite global por proceso de 10 escrituras por minuto (sin cola).

Configurar una clave aleatoria de al menos 32 caracteres en
`Security:AdminApiKey` mediante User Secrets del proyecto Liro.Api en desarrollo,
o `Security__AdminApiKey` en el gestor de secretos del despliegue.
Sin clave, las escrituras quedan cerradas; no existe clave predeterminada.
No enviar esa clave al frontend ni incluirla en la extensión.
El servidor debe recibir HTTPS; no se confía automáticamente en cabeceras de proxies.
Si hay un proxy inverso, configurar explícitamente los proxies de confianza y HTTPS antes de publicar.

Los errores de entrada retornan 400, conflictos 409, proveedor externo 502 y base no disponible 503.
Los DTO no exponen eventos de dominio ni el token de concurrencia.
El diagnóstico detallado /api/infrastructure solo existe funcionalmente en Development y devuelve
503 cuando una dependencia no está disponible.

## Catálogo y solicitudes Roblox

El catálogo admite assets y bundles oficiales (User/1), detalles por lotes y búsquedas
`/v2/search/items/details`. La configuración valida ese creador; `CreatorTargetId=null`
no es una alternativa admitida. La configuración operativa desactiva bootstrap y
recorrido histórico ordinario, y mantiene descubrimiento reciente y vigilancia de limiteds.

Las fuentes de operación son [descubrimiento](docs/CATALOG-DISCOVERY.md),
[monitor de limiteds](docs/LIMITED-MONITOR.md), [prioridades](docs/CATALOG-REFRESH-PRIORITY.md)
y [presupuesto y reutilización](docs/optimizacion-solicitudes-roblox.md).
Estas guías distinguen valores configurados, elegibilidad y tiempos efectivos; un intervalo
no garantiza una consulta puntual. Para 403 ver [diagnóstico](docs/DIAGNOSTICO-403-CATALOGO.md).

El presupuesto directo se coordina en Valkey: hasta 18 solicitudes globales por ventana
de 60 segundos, con un subpresupuesto de 12 búsquedas y separación mínima de tres segundos.
Mercado directo dispone de hasta una solicitud por minuto dentro del total, no adicional.
Los reintentos HTTP internos están desactivados; cada nuevo intento vuelve a pasar por
el presupuesto. El [respaldo directo](MARKET-DIRECT-FALLBACK.md) documenta reservas,
recuperación y pausas 429 compartidas. Los proxies de mercado tienen sus propios presupuestos.

## Outbox y recuperación operativa

Los eventos nuevos usan un nombre de contrato estable; se aceptan los ItemBecameLimited antiguos.
El guardado del item y su outbox se hace en la misma operación EF. Todos los overloads de guardado
capturan eventos, y un guardado fallido conserva el evento sin duplicarlo al reintentar.

El procesador usa transacciones y FOR UPDATE SKIP LOCKED para coordinar múltiples consumidores.
Los reintentos tienen espera creciente y cuarentena después de 8 fallos.
La entrega es al menos una vez: cualquier futuro handler con efectos externos debe ser idempotente.
El handler actual únicamente escribe un log; este handler no envía notificaciones externas.
Las alertas Discord tienen su servicio independiente.

Monitorizar:
- CatalogImportFailures y OutboxMessages con DeadLetteredAtUtc no nulo.
- Atraso del evento pendiente más antiguo.
- LastCheckedAt y el avance/completitud de CatalogCheckpoints.
- HTTP 429/500 de Roblox y conflictos de importación.

Tras corregir la causa de un item apartado, una importación administrativa exitosa limpia su fallo.
Para reintentar un evento apartado, un operador puede restablecer sus campos Attempts,
DeadLetteredAtUtc y NextAttemptAtUtc de forma dirigida, conservando su ID.
No reiniciar en masa mensajes sin revisar la idempotencia del handler.
Para volver a recorrer un alcance ya completado, restablecer su checkpoint de manera dirigida.

La cuota administrativa de entrada es por proceso; no debe confundirse con el presupuesto
saliente compartido en Valkey. Ver [convenciones y límites HTTP](docs/api-route-conventions.md).

## Recuperación de la vigilancia de Aspire 13.6.1

Aspire 13.6.1 incluye la corrección oficial que reintenta las conexiones de vigilancia
DCP tras un timeout y conserva la cancelación del apagado. El AppHost usa esa
implementación nativa; se retiró el adaptador temporal exclusivo de 13.6.0, cuya
comprobación de versión impedía arrancar después de actualizar.

Referencia: https://github.com/microsoft/aspire/releases/tag/v13.6.1

Las pruebas `tests/Liro.AppHost.RegressionTests` verifican la política nativa de conexión
y la recuperación de fin de stream, los errores ajenos y la cancelación. Incluyen
KubernetesClient con un canal HTTP simulado que primero se bloquea y después entrega
un evento. La inspección por reflection queda únicamente en las pruebas; no modifican
las políticas de Aspire ni arrancan Docker, Roblox o PostgreSQL.

El script de verificación enlazado arriba incluye esta suite. Detener la ejecución anterior y reiniciar AppHost
para cargar la corrección. El acceso real al dashboard se comprueba por separado.

## Convenciones y organización

El backend tiene su propio `.editorconfig`: cuatro espacios en C#, llaves en líneas
separadas y bloques de control expandidos. Las migraciones generadas por EF quedan
fuera de la limpieza manual. El script de verificación comprueba el formato antes de compilar
y ejecutar las cuatro suites de regresión.

Las firmas de métodos y constructores con hasta seis parámetros se escriben en una
sola línea. Con más de seis parámetros pueden distribuirse en varias líneas.
Esta regla no compacta las cadenas LINQ ni el cuerpo del método.

Los métodos de consulta usan bloques explícitos, cadenas LINQ en varias líneas y
una variable local con el resultado materializado antes de `return`. Esto permite
inspeccionar el dato y colocar un breakpoint antes de devolverlo. Los filtros,
ordenamientos y límites se mantienen antes de materializar la consulta.

Los modelos de persistencia, configuraciones EF y stores se encuentran en archivos
separados. Se conservan sus namespaces y contratos existentes. Los workers del
catálogo comparten `CatalogWorkerDelay` para la espera por límites de Roblox.

La suite `Liro.RegressionTests` registra casos por área: catálogo, API, persistencia,
workers y Roblox. `Support` contiene las aserciones y dobles de prueba. Sigue siendo un
ejecutable de regresión propio; no se ha migrado a un framework de pruebas.
Las pruebas de bootstrap esperan la tarea del worker con un plazo máximo, sin sleeps
para adivinar cuándo terminó. Las aserciones verifican la URL exacta de miniatura,
los IDs guardados y la propagación del estado HTTP 429.

## Contratos, mercado e historial

La [guía frontend](docs/frontend/README.md) centraliza identidad Asset/Bundle,
precios, disponibilidad, fechas y errores. [Las convenciones HTTP](docs/api-route-conventions.md)
documentan listado y cursor; [las series de mercado](docs/market-history-api.md) describen
RAP y ventas diarias. No confundir esas series con las observaciones de catálogo de
`ItemMarketSnapshots` ni interpretar RAP como una transacción individual.

El seguimiento independiente se documenta en [mercado](docs/MERCADO-SEGUIMIENTO.md);
el comportamiento de respaldo y las prioridades vigentes, en [respaldo directo](MARKET-DIRECT-FALLBACK.md).

Como contexto de evolución, `20261009214903_AddItemMarketObservations` añadió metadatos
y tablas de observaciones; `AddItemMarketplaceUrl` incorporó el enlace generado.
El historial comienza con observaciones guardadas y no reconstruye automáticamente el pasado.
Conservar las migraciones existentes y respaldar la base antes de cambios de esquema.

## Frecuencia de actualización del catálogo

`Catalog:RefreshInterval` configura el intervalo tras una consulta exitosa (30 minutos
por defecto), tanto en API como en Workers. Mantener el mismo valor en ambos procesos.
Se aceptan intervalos de 1 minuto a 24 horas. La revisión histórica ordinaria está
desactivada en la configuración operativa; este intervalo no sustituye la programación
del monitor ni de las colas prioritarias.
El intervalo indica elegibilidad: una cola grande o un 429 pueden retrasar la consulta.

Al iniciar, las programaciones antiguas exactamente a 24 horas desde `LastCheckedAt`
se adelantan al intervalo configurado mediante una actualización LINQ en PostgreSQL.
Se excluyen los objetos con fallos registrados y otras fechas de aplazamiento. La
operación es idempotente y no elimina objetos ni cambia sus datos de mercado.
No se reinterpretan campos NULL como motivo para reintentar continuamente.

## Identidad oficial y fechas observadas

Solo se importan objetos cuyo detalle confirme `creatorTargetId = 1` y `creatorType = User`.
El nombre visible no acredita al creador. Los detalles sin identidad suficiente fallan
para permitir un reintento, sin borrar datos existentes. Un creador ajeno confirmado
se excluye y, si ya estaba almacenado, se elimina junto con su estado de mercado,
snapshots y fallos. El arranque ya no elimina registros por creador ni sus series asociadas;
las operaciones explícitas del importador conservan su política existente.
Los mensajes de outbox se conservan como registro de eventos anteriores.

`RobloxCreatedAtUtc` conserva el primer `itemCreatedUtc` recibido.
`RobloxUpdatedAtUtc` empieza con esa misma fecha y solo avanza si llega un
`itemCreatedUtc` posterior a la creación guardada y a la última fecha observada.
Una fecha ausente, igual o anterior no borra ni hace retroceder las fechas guardadas.
En objetos antiguos, la actualización inicialmente nula se completa desde la creación
conservada durante su siguiente importación exitosa. No representa un campo de
modificación independiente publicado por Roblox ni la hora de consulta de Liro.

El bot independiente de alertas para pruebas y producción está en `src/Liro.Discord.Alerts`. Consulta [configuración y despliegue](docs/discord-alerts.md).
