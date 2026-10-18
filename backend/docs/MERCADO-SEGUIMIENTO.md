# Seguimiento de mercado

## Estado inicial

`Liro.MarketWorker` es un proceso separado. No se lanza por defecto y `Market:Enabled=false`.
No cambia los servicios ni los presupuestos de la principal, conocidos o complementarias.
Sin conexión de mercado válida no programa artículos ni envía solicitudes. No existe salida
directa de respaldo ni rotación automática para eludir un 429. No se incluyen proxies reales.

## Tablas

- `CatalogoGeneral`: Id, Tipo, Clave, Valor, TipoDato, Descripcion, Activo, FechaCreacion,
  FechaModificacion, CreadoPor y ModificadoPor. Única por Tipo + Clave, sensibles a mayúsculas.
  EF valida tipo de valor y actualiza la fecha de modificación. Las ediciones SQL deben mantener
  sus fechas y autor explícitamente. No guardar tokens ni contraseñas en Valor.
- `MarketWatches`: un seguimiento por Item, reserva de cinco minutos, fechas, reintentos,
  último RAP y su fecha independiente de los precios, actividad y próxima revisión.
- `MarketHistoryPoints`: precio medio de venta y volumen tal como los devuelve la fuente.
  Clave única: artículo, fuente, indicador y periodo. Una corrección actualiza el punto existente.
- `MarketRapSnapshots`: cambios de RAP observados por Liro. No son el historial de precios
  medios; el proveedor no expone una serie histórica de RAP en `priceDataPoints`.

Los precios del catálogo y su historial YA se conservan en `ItemMarketStates` y
`ItemMarketSnapshots`. Se reutilizan esas tablas; el trabajador nuevo no vuelve a solicitarlos.

## Ejecución

1. Aplicar la migración `AddMarketTrackingAndGeneralCatalog` mediante el flujo habitual del
   backend (Development la aplica al iniciar API/Workers). El trabajador de mercado no aplica
   migraciones ni ejecuta la limpieza de artículos del arranque del catálogo.
2. Ejecutar `scripts/Configurar-Mercado.sql`: crea una plantilla INACTIVA, idempotente.
3. Configurar una dirección de proxy real y la referencia a sus credenciales. Solo se aceptan
   proxies HTTP/HTTPS con host y puerto, sin usuario/contraseña en la URL, rutas ni parámetros.
4. Guardar `MarketProxySecrets:PROXY_01:Username` y `MarketProxySecrets:PROXY_01:Password`
   mediante User Secrets del proyecto `src/Liro.MarketWorker` en Development o gestor de
   secretos del entorno. Variables equivalentes: `MarketProxySecrets__PROXY_01__Username`
   y `MarketProxySecrets__PROXY_01__Password`. No pegar secretos en SQL, Git ni diagnósticos.
5. Activar el registro en CatalogoGeneral. Se recarga cada minuto (máximo 32 conexiones).
6. Configurar `MarketWorker:Launch=true` y `MarketWorker:Enabled=true` en AppHost y reiniciar
   AppHost cuando se decida iniciar esta etapa. Los valores por defecto son false.
   Ejecución independiente: requiere ConnectionStrings:lirodb, ConnectionStrings:valkey y
   Market:Enabled=true. No es necesario activar ni reiniciar el proceso del catálogo para
   ejecutar este proyecto de forma independiente.

## Recorrido y control de tráfico

Cada cinco minutos se incorporan limitados oficiales (User/1, Limited o LimitedUnique) con
CollectibleItemId. Los que no tienen ese identificador quedan fuera hasta que el catálogo lo
obtenga. No se envían IDs de instancia como IDs de artículo coleccionable.

Se reserva el pendiente con fecha más antigua mediante `FOR UPDATE SKIP LOCKED`. La reserva
evita consultas duplicadas y se recupera tras cinco minutos si el proceso termina. Solo la
reserva vigente puede guardar; la transacción escribe historial y estado juntos.

Primera versión conservadora: una petición activa por proceso; máximo global de MERCADO de
18 por 60 segundos y separación de 3 segundos, además del límite por conexión (plantilla: 6).
Las reservas de Valkey incluyen peticiones en curso y la ventana después de su finalización.
Usa exclusivamente claves `liro:{market}:...`; no consume ni reinicia `liro:{roblox}:...`.
Los límites de todas las instancias deben configurarse iguales. No se presume que más IPs
otorguen cuotas independientes en Roblox. No se promete cumplir intervalos de 2 minutos
para todos con este presupuesto; consultar el atraso en Diagnostico-Mercado.sql.

Un 429 respeta Retry-After (segundos o fecha). Si falta, espera un minuto. Se aplica un
enfriamiento compartido a TODO el mercado para no eludir la espera cambiando de proxy.
Los errores no vacían el RAP. Otros fallos reintentan con espera creciente hasta 180 minutos,
sin descarte permanente. No se reintenta HTTP de forma oculta. Una respuesta vacía o inválida
no se interpreta como RAP cero. No se siguen redirecciones; se conserva validación TLS.

Las consultas usan exclusivamente `/marketplace-sales/v1/item/{collectibleItemId}/resale-data`.
Se conservan todos los puntos devueltos, no se promete recuperar periodos ausentes del proveedor.
La actividad necesita siete días completos explícitos de volumen diario; no convierte días
faltantes en ventas cero. Los umbrales iniciales son operativos, NO una puntuación de demanda:
100+ ventas/día: 2 min; 10+: 3 min; 1+: 30 min; menos de 1: 120 min; cero: 180 min;
datos insuficientes: 120 min. Frecuencias configurables en Market, no migradas al catálogo general.

## Observabilidad y alcance

Los logs de aplicación muestran ID interno, código de resultado y número de puntos, nunca
contraseñas ni URL de proxy. Consultar `scripts/Diagnostico-Mercado.sql` para cobertura y atraso.
El registro se desactiva para retirar una conexión en la siguiente recarga; una consulta ya
iniciada puede completar. El proceso comparte CPU, disco y base con el catálogo: la independencia
de red no garantiza impacto cero al habilitarlo. Medir antes de ampliar capacidad.

No incluye todavía API pública de RAP, gráficas, SignalR de mercado, cálculo de demanda/valoración,
administrador web de CatalogoGeneral ni migración de otras constantes. La lectura de precios
existente sigue funcionando. No activar contra Roblox sin configurar y probar primero las salidas.
