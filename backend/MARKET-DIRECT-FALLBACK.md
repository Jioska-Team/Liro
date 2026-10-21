# Respaldo directo de mercado

Mercado prefiere los proxies configurados. Si todos están temporalmente indisponibles (cuota HTTP 402, conexión fallida o timeout), utiliza la IP del backend. Si no hay proxies configurados también puede utilizar el respaldo. Cuando vence la pausa de un proxy, vuelve a probarlo; si falla otra vez, retorna al respaldo. Los HTTP 403/429 de Roblox pausan mercado completo y no habilitan cambios de ruta para eludir esa pausa.

Configuración de `Market`:

- `DirectFallbackEnabled`: `true` por defecto; `false` conserva el funcionamiento exclusivo con proxies.
- `DirectRequestsPerMinute`: `1` por defecto; admite únicamente 1 o 2. No aumentar sin medir el efecto en catálogo.

El respaldo utiliza `Roblox:Requests:BudgetKey`, la misma ventana de 60 segundos y capacidad que catálogo/API. La capacidad compartida predeterminada es 18; no se agregan otras 18 para mercado. Un script atómico en Valkey anuncia demanda únicamente cuando mercado tiene un artículo reservado y le corresponde otra consulta. Mientras espera, el limitador común deja una plaza para mercado: hasta 17 para el resto y una para mercado, sin cancelar consultas ya iniciadas. Mercado respeta el espaciado común y espera las solicitudes en curso. La ventana se conserva durante 60 segundos después de completar cada petición, por lo que 1/min es un máximo y no una frecuencia exacta.

Al atender mercado, desaparece la reserva de demanda y el resto puede usar todas las plazas que estén libres. Durante el minuto de espera de mercado no se retiene una plaza vacía. La demanda no renovada caduca en cinco segundos, también si se detiene mercado, falla o vuelve a proxies. No se resetean contadores. Un fallo de Valkey impide enviar solicitudes sin contabilizar.

Esto reemplaza la protección anterior de seis plazas y seis segundos libres, que impedía avanzar a mercado con catálogo ocupado. Repartir un presupuesto saturado puede ralentizar el catálogo. Sus prioridades internas de principal/conocidos/secundarios se conservan; este cambio no implementa cuotas iguales por cada tipo interno.

Aplica a `Liro.Api`, `Liro.CatalogWorker` y mercado directo que usan la misma configuración de Valkey y clave de presupuesto. Catálogo, descubrimiento, componentes e imágenes usan los clientes controlados de Infrastructure. Los bots de Discord no consumen este presupuesto Roblox. Los proxies mantienen presupuestos independientes. Una nueva integración debe usar el limitador común: no se interceptan automáticamente clientes HTTP externos ni otras instalaciones.

Para desplegar cambios al limitador compartido deben actualizarse los tres procesos consumidores. No basta con reiniciar mercado si API/catálogo conservan una versión antigua de Infrastructure.

Se conserva un solo trabajador, la cola de `MarketWatches`, sus reservas exclusivas por artículo y el guardado transaccional existente. No se crean tablas ni se elimina historial. Las frecuencias se recalculan para la menor capacidad durante el respaldo; se conserva la selección de uno de cada cuatro turnos para artículos de menor actividad.

Con una consulta por minuto, 2.554 artículos requerirían como mínimo 2.554 minutos (42 h 34 min) para una única visita a cada uno. En operación real será más: los artículos activos se repiten, hay latencia, espaciado y pausas por errores. Con proxies operativos vuelve la capacidad configurada para ellos.

Diagnóstico en logs: `Ruta de mercado: DIRECTA_RESPALDO`, solicitudes con ruta `DIRECTA_RESPALDO`, y `guardado True` con conexión `DIRECTA_RESPALDO`. `CupoDirectoPrioridadCatalogo` indica espera por capacidad, no error de Roblox. `PROXIES` indica vuelta a las rutas proxy (no garantiza que su cuota esté restablecida hasta que una consulta tenga éxito).

El control de inicio/Stop en Discord sigue actuando sobre el mismo proceso. No se necesita otro bot.

## Recuperación de reservas

Los resultados ambiguos de Valkey se reconcilian antes de reservar otro turno. Mercado conserva su instancia de presupuesto directo entre intentos; el cierre del presupuesto directo y del compartido es atómico e idempotente. Si no se puede confirmar el cierre, no se elimina el contador ni se envía una consulta sin cupo.

Las reservas nuevas llevan una validez de cinco minutos en Valkey (`:leases`). Antes de cada envío, catálogo, búsquedas y mercado comprueban todas sus reservas y limitan la operación HTTP a un máximo de treinta segundos, dejando un minuto de margen respecto de la caducidad. Una reserva que vence mientras esperaba no autoriza una solicitud: se cierra y el ciclo puede reintentarlo.

El siguiente intento de adquisición recupera las reservas nuevas vencidas, incluso de otro host, contabilizándolas como completadas en ese momento durante una ventana completa. La recuperación no concede capacidad instantánea ni aumenta 18 globales, 1/min directo o el límite configurado de cada proxy. Las reservas antiguas sin plazo no se borran por antigüedad estimada: solo se recuperan automáticamente cuando se comprueba que su proceso local terminó. Las antiguas de otro host requieren verificación explícita.

La inicialización de la base de datos ya no elimina artículos por creador. Reiniciar API o catálogo conserva los artículos y sus series asociadas; no se añade retención ni depuración automática. Las operaciones explícitas de eliminación existentes no cambian. No hay migraciones ni modificación de los datos existentes para activar esta corrección.
# Pausas compartidas por HTTP 429

Los consumidores directos de la API, catálogo (principal y conocidos) y mercado comparten
la pausa en Valkey bajo `Roblox:Requests:BudgetKey` más `:cooldown`. Deben usar la misma
instancia de Valkey y clave de presupuesto cuando salen por la misma IP; no se detecta
automáticamente que dos instalaciones comparten una IP.

Un HTTP 429 publica la pausa antes de liberar su reserva. Se respeta `Retry-After`
(segundos o fecha); sin un plazo válido se usa la espera de respaldo. Una pausa nueva
solo puede prolongar la vigente. Las reservas y los contadores no se reinician.
Se comprueba la pausa al reservar y justo antes de enviar una solicitud ya reservada.
Las solicitudes que ya estaban en vuelo pueden terminar. Un 200 o un 403 de CSRF
no elimina ni crea esta pausa compartida.

Sin conexiones de proxy configuradas, mercado usa el respaldo directo existente:
una solicitud por minuto dentro de las 18 globales, no adicional a ellas.
Los cambios de código requieren reiniciar el backend; las conexiones de mercado
se recargan sin reiniciar.

## Prioridad de los artículos más activos

En los turnos prioritarios, los artículos `MuyAlta` ya vencidos se seleccionan antes
que los `Alta`, aunque estos últimos tengan un vencimiento más antiguo. Dentro de
cada grupo se conserva el orden de vencimiento y el identificador como desempate.
Uno de cada cuatro turnos conserva la preferencia por los demás grupos.
Las reservas vigentes y las esperas por errores del proveedor siguen excluyendo
artículos de la selección; la prioridad no permite saltar un 429 ni consumir cupo extra.

Con los cinco artículos de actividad muy alta y el cupo directo de una solicitud
por minuto, el plan existente calcula un objetivo de 15 minutos. Es un vencimiento,
no una promesa de envío exacto: hay que esperar el siguiente turno disponible,
la consulta en curso y cualquier pausa compartida. El orden corregido elimina
la demora adicional causada por el atraso de actividad alta.
