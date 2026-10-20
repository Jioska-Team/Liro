# Respaldo directo de mercado

Mercado prefiere los proxies configurados. Si todos están temporalmente indisponibles (cuota HTTP 402, conexión fallida o timeout), utiliza la IP del backend. Si no hay proxies configurados también puede utilizar el respaldo. Cuando vence la pausa de un proxy, vuelve a probarlo; si falla otra vez, retorna al respaldo. Los HTTP 403/429 de Roblox pausan mercado completo y no habilitan cambios de ruta para eludir esa pausa.

Configuración de `Market`:

- `DirectFallbackEnabled`: `true` por defecto; `false` conserva el funcionamiento exclusivo con proxies.
- `DirectRequestsPerMinute`: `2` por defecto; admite únicamente 1 o 2.

El respaldo utiliza `Roblox:Requests:BudgetKey`, la misma ventana de 60 segundos y capacidad que catálogo/API. La capacidad compartida predeterminada es 18; no se agregan otras 18 para mercado. Un script atómico en Valkey impide el envío si hay solicitudes en curso, si se consumirían las últimas seis plazas del presupuesto, o si no han transcurrido seis segundos desde la última respuesta directa. También limita y espacia las consultas de respaldo. Un fallo de Valkey impide enviar solicitudes sin contabilizar.

Es prioridad oportunista: una solicitud del catálogo que llegue después de reservar mercado puede esperar el espaciado compartido. No promete impacto cero ni velocidad mínima. Si el catálogo está ocupado, mercado espera y puede no alcanzar dos consultas por minuto.

Se conserva un solo trabajador, la cola de `MarketWatches`, sus reservas exclusivas por artículo y el guardado transaccional existente. No se crean tablas ni se elimina historial. Las frecuencias se recalculan para la menor capacidad durante el respaldo; se conserva la selección de uno de cada cuatro turnos para artículos de menor actividad.

Con dos consultas por minuto, 2.554 artículos requerirían como mínimo 1.277 minutos (21 h 17 min) para una única visita a cada uno. En operación real será más: los artículos activos se repiten, hay latencia y el catálogo tiene preferencia. Con proxies operativos vuelve la capacidad configurada para ellos.

Diagnóstico en logs: `Ruta de mercado: DIRECTA_RESPALDO`, solicitudes con ruta `DIRECTA_RESPALDO`, y `guardado True` con conexión `DIRECTA_RESPALDO`. `CupoDirectoPrioridadCatalogo` indica espera por capacidad, no error de Roblox. `PROXIES` indica vuelta a las rutas proxy (no garantiza que su cuota esté restablecida hasta que una consulta tenga éxito).

El control de inicio/Stop en Discord sigue actuando sobre el mismo proceso. No se necesita otro bot ni iniciar el sandbox.
