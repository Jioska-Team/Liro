# Recuperación histórica independiente

Objetivo: recuperar fechas de precio medio y volumen de los limiteds oficiales de Roblox, sin cambiar la búsqueda principal, conocidos, complementarias ni mercado. Es una herramienta de ejecución finita; AppHost inicia automáticamente la recuperación de Internet Archive como proceso independiente. No consulta Roblox, no emplea sus proxies ni sus presupuestos y no modifica RAP, precios actuales o la programación de mercado.

## Procedencia y acceso

Rolimon’s restringe la extracción automatizada y la reutilización de datos en https://www.rolimons.com/termsandprivacy. El acceso técnico a una página no concede permiso. Las descargas remotas vienen desactivadas y requieren habilitación explícita más una referencia al permiso de la fuente en `HistoryRecoveryControl`. No se habilitan por reiniciar el backend. La herramienta puede validar e importar archivos locales sin hacer solicitudes externas.

## Tablas y consulta

- `HistoryRecoveryJobs`: cola por ID interno, estado, intentos, fechas de cobertura, discrepancias, URL y huella SHA-256 del archivo utilizado.
- `MarketHistoryArchivePoints`: archivo separado con clave única por artículo, métrica y fecha; conserva la fuente y fecha de obtención.
- `HistoryRecoveryControl`: habilitación y próxima descarga permitida.
- `MarketHistoryCombined`: vista con un único punto por artículo/métrica/día UTC. Roblox tiene prioridad, incluso si sus datos llegan después de importar el archivo. Los puntos previos de Fiery Horns en `MarketHistoryPoints` también están incluidos.

El archivo conserva evidencia de la fuente y puede coincidir con datos que Roblox descargue posteriormente. La vista elimina ese solapamiento lógico; las gráficas y consultas deben usarla, nunca sumar directamente ambas tablas. El trabajador de mercado sigue escribiendo exclusivamente en su tabla actual. Esta implementación no cambia los endpoints existentes para servir la vista automáticamente.

## Preparación y ejecución

Asignar `LIRO_HISTORY_CONNECTION` mediante el gestor de secretos o entorno de ejecución; no poner contraseñas en comandos, argumentos o archivos versionados. Compilar:

```powershell
dotnet build tools/Liro.HistoryRecovery -m:1 -p:NuGetAudit=false
```

Ejecutar la DLL `tools/Liro.HistoryRecovery/bin/Debug/net10.0/Liro.HistoryRecovery.dll` con uno de estos argumentos:

- `init`: instala las tablas/vista de forma aditiva y crea trabajos para artículos oficiales limited de tipo Asset y Bundle. No altera tablas existentes.
- `status`: muestra estado agregado sin realizar descargas ni importar.
- `run --cache RUTA --limit 10`: procesa hasta diez archivos locales pendientes. La cola se amplía con nuevos limiteds en cada ejecución. Los archivos ausentes quedan pendientes.
- `run --cache RUTA --limit 10 --download`: permite descargar archivos ausentes solo si `AllowRemoteDownloads=true` y `PermissionReference` está informado. Antes debe existir autorización de la fuente para el uso previsto. Es un parámetro de operación, no una autorización por sí mismo.

Cada archivo se llama `Asset-ID.json` o `Bundle-ID.json` y contiene `ItemType`, `RobloxId`, `SourceUrl`, `FetchedAtUtc` en UTC y `SalesData` con `num_points`, `timestamp`, `avg_daily_sales_price`, `sales_volume`. Las descargas generan este formato automáticamente. Se publican por renombrado atómico; un archivo `.tmp` no se importa. La identidad y los vectores completos se validan antes de escribir. Un cambio de formato de página provoca error y revisión; nunca se interpreta automáticamente como cero ventas.

## Límites y recuperación

Una conexión PostgreSQL y una instancia por base mediante bloqueo consultivo. Descarga secuencial, mínimo 15 segundos entre comienzos (persistido). Sin cookies, redirecciones ni rotación de proxies. Máximo 8 MiB por respuesta y 10.000 fechas por serie. Ante 403/429 se conserva el progreso y se termina la ejecución respetando `Retry-After`; los procesos actuales no participan en esta espera.

Escrituras de hasta 120 puntos, pausa de un segundo entre grupos, espera de bloqueo máxima 250 ms y límite de 3 segundos por instrucción del lote. Antes de cada grupo se comprueba la carga: si existen cuatro consultas activas ajenas o alguna espera por bloqueo en la misma base, se detiene la recuperación. Ante error de base también se detiene: no sigue enviando carga. Los lotes confirmados se conservan, y reanudar no los duplica. Las transacciones cortas reducen impacto pero no garantizan carga cero: comenzar con lotes de artículos pequeños y observar latencias, bloqueos y errores antes de ampliar.

No se sobrescribe ningún punto existente. Si una fecha compartida con Roblox difiere, el artículo queda en `Revisar` y no se importan sus puntos. Los errores de fuente quedan pendientes 15 minutos y se intentan hasta tres veces; corregir el problema antes de restablecer un trabajo concreto a `Pendiente` con `Attempts=0`. Los artículos completados no se descargan de nuevo. Para revisar un artículo completado se puede restablecer su trabajo deliberadamente; la clave evita duplicar la importación.

La consulta `scripts/Consultar-Historial-Completo.sql` incluye ambas fuentes con prioridad de Roblox. El diagnóstico está en `scripts/Diagnostico-Recuperacion-Historica.sql`. Una primera fecha antigua no demuestra la fecha exacta de conversión a limited; los huecos no se rellenan con cero.

## Pruebas

El proyecto ejecutable `tests/Liro.HistoryRecovery.Tests` sigue el estilo de pruebas del repositorio. Valida datos, identidad, filas repetidas, volumen y fechas. Con `--database` y `LIRO_HISTORY_TEST_CONNECTION` crea un esquema aislado aleatorio y comprueba repetición, prioridad posterior de Roblox, exclusión entre instancias, interrupción/reanudación y detención por bloqueos. Solo elimina ese esquema temporal al terminar; no modifica datos de catálogo.

## Recuperación desde Internet Archive

La cola `InternetArchiveJobs` busca los artículos pendientes mediante el índice CDX paginado. Consulta las rutas históricas de reventas de Roblox (por ID de artículo y, cuando existe, por identificador coleccionable). No limita las capturas a una por año. `InternetArchiveCaptures` conserva cada URL, fecha, estado, intentos y huella SHA-256; el cursor del índice queda guardado para reanudar.

Comandos adicionales de la misma DLL:

- `archive-import --cache RUTA --limit 10`: importa archivos locales `*.capture.json`, sin solicitudes externas.
- `archive-enable`: habilita únicamente las consultas a Internet Archive.
- `archive-run --cache RUTA --limit 1000000 --download`: busca capturas e importa sus series de manera gradual. AppHost utiliza este modo automáticamente; no iniciar otra instancia manual mientras esté activo.
- `archive-status`: muestra el avance de índices, capturas y puntos guardados.
- `archive-stop`: deshabilita nuevas consultas; la ejecución termina al comprobar el control. Ctrl+C permite cancelar la ejecución inmediatamente.

Cada archivo contiene `RobloxId`, `ItemType`, `OriginalUrl`, `CapturedAtUtc`, `FetchedAtUtc` y `Payload` con la respuesta histórica. Se validan identidad, ruta y fechas antes de importar. Precio y volumen se procesan independientemente: una métrica ausente no se rellena con cero.

Los puntos se guardan en `MarketHistoryInternetArchivePoints`, con fecha diaria UTC, marca temporal original, URL de captura, fecha de obtención y huella. La vista `MarketHistoryCombined` ahora devuelve un punto por artículo, métrica y **día UTC**, dando prioridad a los registros actuales de Roblox incluso cuando llegan después con otra hora del mismo día. No se modifican las tablas originales. Las discrepancias se registran sin sobrescribir; se pueden importar los otros puntos que faltan en esa captura.

El proceso comparte la exclusión del importador anterior y usa una sola conexión. Las solicitudes van exclusivamente a `web.archive.org`, sin proxies ni redirecciones a Roblox, con al menos 15 segundos entre comienzos. Ante carga de la base espera y reanuda; los lotes tienen hasta 120 puntos con un segundo entre lotes. Un 403 deshabilita la descarga; un 429 o error de servidor aplaza las consultas respetando `Retry-After` (15 minutos si no está indicado). Se conservan los trabajos y se permiten hasta tres intentos por página o captura. Los errores de base detienen la ejecución para no insistir sobre los procesos prioritarios.

`IndiceRecorrido` solo significa que se agotaron las páginas de las rutas consultadas. **No demuestra que se haya recuperado todo el historial**, ni la fecha de conversión a limited. Las capturas pueden faltar, contener series vacías o cubrir periodos parciales. La descarga de Rolimon’s sigue bajo su control independiente y no se habilita con estos comandos.


## Inicio y parada con el backend

AppHost administra el recurso `history-recovery` como proyecto independiente. Espera a PostgreSQL y recibe su conexión activa por entorno; no usa la conexión de una ejecución anterior ni hace esperar a la API, conocidos o mercado. Discord detiene el AppHost mediante Aspire, que también detiene este proceso.

En cada arranque ejecuta `archive-run --download --limit 1000000`. Conserva el control de habilitación de `InternetArchiveControl`: un reinicio no anula una desactivación manual ni un bloqueo por 403. Reutiliza los cursores y capturas confirmados; el bloqueo consultivo impide dos importadores activos y las claves únicas evitan duplicados aunque una parada interrumpa una captura entre lotes. La caché se encuentra en `%LOCALAPPDATA%\Liro\HistoryRecovery`; el progreso autoritativo está en PostgreSQL.

Para pasar de una ejecución manual al proceso administrado: detener la ejecución manual con `archive-stop`, esperar su salida y habilitar con `archive-enable` antes del próximo inicio del backend. No dejar un importador manual activo: el proceso de Aspire rechazará el segundo inicio mientras el bloqueo esté ocupado.

El proceso termina si agota los trabajos, si se deshabilita la descarga o si hay un error de base; vuelve a intentarlo en el siguiente arranque del backend. Esto integra el ciclo de vida, no introduce un supervisor de reinicios ante fallos dentro de la misma sesión. La parada no borra historial ni cursores.


## Reparto de capturas y escrituras evitadas

Las capturas se distribuyen por vueltas: se elige primero el artículo con menos intentos de captura acumulados. Dentro del mismo número de turnos se prioriza al que todavía no tiene precio medio ni volumen en ninguna de las tres tablas históricas; un dato de RAP no cuenta como historial de ventas. Después se usa el turno más antiguo y un desempate estable. Los turnos se derivan de los registros ya guardados, así que sobreviven a un reinicio sin otra tabla ni migración. Las capturas vacías y los fallos también consumen turno; se mantienen las fechas de reintento y el máximo de tres intentos. Ninguna captura se descarta por este cambio.

La búsqueda de índices también da preferencia a los artículos sin historial entre los que están en la misma ruta. Primero avanza la ruta menos recorrida, después los artículos sin datos en ninguna fuente y finalmente el intento más antiguo. Al completar una ruta, el artículo deja paso a los restantes de la vuelta. La prioridad se calcula con los datos actuales: no mantiene una lista fija de 57 ni reabre capturas ya procesadas. Continúan las búsquedas de los demás artículos para localizar periodos o métricas faltantes; no se presupone que una fecha sin registros implique una venta perdida.

Antes de escribir, se consulta una vez el historial combinado del artículo en el intervalo de la captura. Solo las métricas/fechas ausentes pasan por lotes de hasta 120. Una captura totalmente solapada no abre transacciones de inserción ni esperas entre esos lotes. Se mantiene la comprobación SQL de existencia y la clave única para las llegadas concurrentes de mercado. Las diferencias no se sobrescriben. El registro de la captura solo se actualiza si cambian su estado, huella, número de puntos, diferencias o error; la primera finalización sigue persistiendo su progreso.

Se conservan los 15 segundos entre solicitudes, la alternancia entre índice y descarga, el bloqueo de instancia y la cesión ante carga de base. Estas mejoras distribuyen antes los beneficios entre artículos y reducen escrituras; no aumentan el presupuesto de Internet Archive ni garantizan una fecha de recuperación completa.
