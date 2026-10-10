# Desarrollo y operación del backend Liro

## Verificación

Desde backend: `./verify.ps1`.
Compila la solución y ejecuta `tests/Liro.RegressionTests`, un ejecutable de regresión sin paquetes adicionales de test.
También puede ejecutarse con `dotnet run --project tests/Liro.RegressionTests`.
No usar un resultado vacío de `dotnet test` como prueba: este proyecto usa un ejecutable, no un adaptador de test.

Las pruebas cubren dominio, importación con HTTP simulado, reanudación del bootstrap, validación MVC,
autenticación, serialización de eventos y coherencia entre modelo/migraciones.
No sustituyen una prueba con PostgreSQL, Docker y Roblox reales.

## Arranque y migraciones

Ejecutar Liro.AppHost con Docker disponible. API y Workers registran primero
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

## Catálogo y errores HTTP 500 de Roblox

Los clientes HTTP conservan reintentos para fallos transitorios de red/5xx, con una política específica para Roblox. Cuando Roblox continúa
devolviendo 500 tras esos reintentos, el bootstrap espera RetryDelay y retoma el cursor persistido.
No se oculta el fallo externo ni se promete que sus servidores responderán correctamente.
Un cursor rechazado con 400 reinicia el recorrido del alcance configurado.
Las páginas incompletas o con cursor que no avanza se rechazan; no se interpretan como fin exitoso.

El filtro por creador de Roblox (ID 1) se conserva por defecto. Se configura con:
`Roblox:Catalog:CreatorTargetId`, `CreatorType`, `Category` y `PageSize`.
En JSON, CreatorTargetId=null elimina el filtro de creador.
Bootstrap y descubrimiento periódico utilizan el mismo alcance y endpoint v1 documentado.
Cambiar el alcance produce un checkpoint separado.
Una búsqueda paginada no garantiza por sí sola cobertura de items que Roblox no publica en ella.

CatalogWorkers configura BootstrapEnabled, RefreshEnabled, DiscoveryEnabled, RetryDelay,
ItemDelay, PageDelay, CycleInterval, BatchSize y DiscoveryPages.
Por defecto los tres trabajos están habilitados, con 2 segundos entre items y ciclos de 5 minutos.
LastCheckedAt registra consultas exitosas; LastUpdatedAt registra cambios de catálogo.
Las imágenes pendientes o los fallos de miniaturas conservan la URL anterior.
Los datos de un item se vuelven a comprobar cuando transcurren 30 minutos (intervalo configurable).

CatalogCheckpoints conserva el cursor de cada alcance. Un bootstrap completado no se repite en cada
reinicio; descubrimiento y refresh mantienen el catálogo.
CatalogImportFailures conserva fallos individuales, los reintenta de forma independiente y los
aparta después de 8 fallos. Esto permite avanzar de página sin perder los items fallidos.
RefreshEnabled también controla el procesador de estos reintentos.

## Outbox y recuperación operativa

Los eventos nuevos usan un nombre de contrato estable; se aceptan los ItemBecameLimited antiguos.
El guardado del item y su outbox se hace en la misma operación EF. Todos los overloads de guardado
capturan eventos, y un guardado fallido conserva el evento sin duplicarlo al reintentar.

El procesador usa transacciones y FOR UPDATE SKIP LOCKED para coordinar múltiples consumidores.
Los reintentos tienen espera creciente y cuarentena después de 8 fallos.
La entrega es al menos una vez: cualquier futuro handler con efectos externos debe ser idempotente.
El handler actual únicamente escribe un log; no se implementa SignalR ni notificaciones externas.

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

La clave administrativa por proceso y el ritmo local de llamadas no son cuotas distribuidas.
Antes de multiplicar réplicas, coordinar el presupuesto global de llamadas a Roblox y el trabajo de
bootstrap; las protecciones de concurrencia evitan sobrescrituras silenciosas pero no eliminan
solicitudes HTTP duplicadas.

## Límites HTTP 429 de Roblox

Todos los clientes Roblox de un mismo proceso comparten una única puerta de solicitudes:
una operación en vuelo y al menos 3 segundos entre operaciones. Este valor es una política
conservadora de Liro, no una cuota oficial garantizada de Roblox.
La puerta envuelve la resiliencia HTTP: la espera local no consume el timeout de red.
Las llamadas de red y los reintentos 5xx de una operación se completan antes de admitir otra.

Un 429 no se reintenta inmediatamente. Se respeta Retry-After (segundos o fecha HTTP);
si no viene una espera positiva, se espera 1 minuto, luego 2, 4, etc., hasta 15 minutos.
Una respuesta exitosa restablece ese contador. Durante esa pausa los demás clientes fallan
localmente con la misma fecha de reanudación, sin enviar tráfico a Roblox.
Los Workers esperan antes de continuar; el 429 no consume intentos de un item ni lo manda
a cuarentena. La API responde 503 con Retry-After cuando el proveedor está limitado.

Configurar Roblox:Requests:MinimumInterval, DefaultCooldown y MaximumFallbackCooldown.
La configuración de Workers está en appsettings.json; los mismos valores por defecto se
usan en la API. Los plazos indicados por Retry-After no se recortan al máximo de respaldo.

El control es por proceso y en memoria. API, otra instancia de Workers, aplicaciones ajenas
o una IP compartida pueden contribuir al límite del proveedor. No es una cuota distribuida
ni evita todos los 429; no reiniciar procesos para intentar eludir el plazo del servidor.
Los rechazos externos permanecen visibles en los logs en lugar de ocultarlos.

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

`verify.ps1` ejecuta ambas suites. Detener la ejecución anterior y reiniciar AppHost
para cargar la corrección. El acceso real al dashboard se comprueba por separado.

## Convenciones y organización

El backend tiene su propio `.editorconfig`: cuatro espacios en C#, llaves en líneas
separadas y bloques de control expandidos. Las migraciones generadas por EF quedan
fuera de la limpieza manual. `verify.ps1` comprueba el formato antes de compilar
y ejecutar las dos suites de regresión.

Las firmas de métodos y constructores con hasta seis parámetros se escriben en una
sola línea. Con más de seis parámetros pueden distribuirse en varias líneas.
Esta regla no compacta las cadenas LINQ ni el cuerpo del método.

Los métodos de consulta usan bloques explícitos, cadenas LINQ en varias líneas y
una variable local con el resultado materializado antes de `return`. Esto permite
inspeccionar el dato y colocar un breakpoint antes de devolverlo. Los filtros,
ordenamientos y límites se mantienen antes de materializar la consulta.

Los modelos de persistencia, configuraciones EF y stores se encuentran en archivos
separados. Se conservan sus namespaces y contratos existentes. Los tres workers del
catálogo comparten `CatalogWorkerDelay` para la espera por límites de Roblox.

La suite `Liro.RegressionTests` registra casos por área: catálogo, API, persistencia,
workers y Roblox. `Support` contiene las aserciones y dobles de prueba. Sigue siendo un
ejecutable de regresión propio; no se ha migrado a un framework de pruebas.
Las pruebas de bootstrap esperan la tarea del worker con un plazo máximo, sin sleeps
para adivinar cuándo terminó. Las aserciones verifican la URL exacta de miniatura,
los IDs guardados y la propagación del estado HTTP 429.

## Metadatos e historial comercial

`Items.MarketplaceUrl` contiene el enlace estable `https://www.roblox.com/catalog/{RobloxAssetId}`
y se expone también en la API. No depende del nombre traducido ni del slug. Es una columna
generada y almacenada por PostgreSQL: la migración `AddItemMarketplaceUrl` completa también
los objetos existentes, sin volver a importarlos. En memoria, el constructor genera el mismo
enlace. No es un campo editable. El SQL de generación pertenece al esquema EF, no a una consulta.
El importador actual acepta assets; los bundles usan otra identidad y `/bundles/{id}` y no
se importan ni se etiquetan como assets en esta fase.

`Items` conserva los registros existentes y añade descripción, creador (ID/tipo/nombre),
tipo de asset, restricciones originales y fechas del objeto en Roblox. `MarketStatus`
conserva los valores anteriores y añade `LimitedUnique = 3` y `Collectible = 4`.
`collectibleItemId` no basta para clasificar un objeto como Limited. Las restricciones
se conservan también como colección; una clasificación resumida no las sustituye.

`ItemMarketState` guarda la última observación comercial por objeto. `ItemMarketSnapshots`
guarda las observaciones anteriores, con fecha UTC y fuente. El importador guarda la ficha,
el estado, el snapshot y los eventos del outbox en un mismo `SaveChanges`. El control de
concurrencia del agregado sigue siendo `Items.xmin`; escribir directamente solo en la tabla
de estado omitiría esa protección y no es una ruta admitida por la aplicación.

Los campos de precio tienen significados independientes:
- `PrimaryPriceRobux`: precio publicado de venta primaria, no precio de lanzamiento.
- `PriceBeforeDiscountRobux`: referencia anterior al descuento actual.
- `LowestPriceRobux`: valor reportado por Roblox, conservado sin reinterpretarlo.
- `LowestResalePriceRobux`: precio mínimo de reventa reportado.

La API añade `market` a la respuesta de los objetos. Incluye los datos observados,
`observedAtUtc`, `source`, `availability` y `availablePriceRobux`. La disponibilidad es
un resumen, no una garantía de compra para todo usuario (pueden existir restricciones
por ubicación o cuenta). `NoSellers` corresponde a la etiqueta visual "No Sellers";
`OffSale` corresponde a "Off Sale". Datos ausentes permanecen nulos; cero no significa
"sin precio". Un fallo HTTP/429 no genera un snapshot ni borra el estado conocido.
Una respuesta correcta pero parcial puede contener valores desconocidos: el histórico
conserva la observación anterior, sin presentar su precio como si acabara de verificarse.

`GET /api/v1/items/{id}/market-history?limit=100&before=2026-10-10T00:00:00Z`
retorna observaciones de más reciente a más antigua. El límite válido es 1–200.
Para la siguiente página se envía como `before` el `observedAtUtc` de la última fila;
el cursor es exclusivo. Se limita y ordena en PostgreSQL antes de materializar.

Los workers existentes completan los objetos antiguos gradualmente según su próxima
actualización programada (30 minutos por defecto; sujeto a cola y límites de Roblox). El histórico comienza con las nuevas
observaciones, sin reconstruir artificialmente el pasado. Esta fase no añade RAP,
precio de última transacción, ventas diarias ni propietarios. Las cantidades total y
disponible no se convierten en "copias vendidas". La fecha de modificación en Roblox
permanece nula porque el endpoint verificado no la documenta.

Fuente: POST `https://catalog.roblox.com/v1/catalog/items/details`, contrato oficial
`https://catalog.roblox.com/docs/json/v1`. Se usa una petición de detalle por objeto;
el contrato permite lotes, pero el planificador actual sigue procesando por objeto.
Se mantienen el control compartido de tráfico y la pausa por HTTP 429.

### Migración de esta ampliación

`20261009214903_AddItemMarketObservations` añade ocho columnas a `Items` y crea las
 dos tablas comerciales. No elimina ni reconstruye tablas existentes. Se revisó el
SQL generado y se comprueba que el modelo y el snapshot EF coincidan.

El arranque actual aplica migraciones automáticamente en Development; en otros entornos
exige que estén aplicadas. Antes de reiniciar con esta versión sobre datos importantes,
realizar una copia de seguridad de `lirodb`. No iniciar una copia de trabajo del AppHost
para validar la migración. La validación automatizada usa respuestas HTTP simuladas y
no sustituye una prueba de migración y concurrencia sobre una copia de PostgreSQL.

## Frecuencia de actualización del catálogo

`Catalog:RefreshInterval` configura el intervalo tras una consulta exitosa (30 minutos
por defecto), tanto en API como en Workers. Mantener el mismo valor en ambos procesos.
Se aceptan intervalos de 1 minuto a 24 horas. El worker conserva los lotes de 50,
la pausa entre ciclos de 5 minutos, el control de peticiones y los cooldowns de Roblox.
El intervalo indica elegibilidad: una cola grande o un 429 pueden retrasar la consulta.

Al iniciar, las programaciones antiguas exactamente a 24 horas desde `LastCheckedAt`
se adelantan al intervalo configurado mediante una actualización LINQ en PostgreSQL.
Se excluyen los objetos con fallos registrados y otras fechas de aplazamiento. La
operación es idempotente y no elimina objetos ni cambia sus datos de mercado.
No se reinterpretan campos NULL como motivo para reintentar continuamente.
