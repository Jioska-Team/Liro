# Cola de mercado por actividad

La selección concede tres de cada cuatro turnos a artículos de actividad alta o muy alta y reserva uno al resto. Si el grupo preferido no tiene pendientes, el turno puede utilizarlo el otro grupo. Dentro de cada grupo se atiende primero la fecha efectiva más antigua. Las reservas PostgreSQL siguen evitando duplicados.

El plan se recalcula cada minuto con la población elegible y la suma de los cupos de los proxies activos. Usa como máximo el 90 % del menor presupuesto entre conexiones y mercado; reserva el 25 % de esa capacidad al fondo. Con 9 de actividad muy alta, 81 alta y cinco proxies de 6/minuto: objetivo de 2 minutos y 6 minutos respectivamente. Son objetivos de programación; la consulta, las esperas y el guardado añaden demora.

Los grupos de menor actividad también ajustan sus intervalos al cupo reservado. Esto alarga la vuelta general respecto al barrido anterior, para permitir actualizaciones frecuentes de los más activos. No se promete una vuelta general de 92 minutos con esta distribución.

Si falta cupo, se conserva la reserva y el turno durante esperas de hasta dos minutos. Si hay que liberarla, NextCheckAtUtc no cambia y LeaseUntilUtc impide reintentar antes de que venza la espera. Los fallos reales y las respuestas del proveedor conservan su tratamiento de espera. No se envían solicitudes por el mero hecho de reintentar adquirir cupo.

Los límites de Redis permanecen intactos: 30 solicitudes por ventana de 60 segundos para mercado y 6 por proxy. No se modifica el trabajador principal ni el de conocidos. No requiere migración.

## Validación

| Requisito | Evidencia |
|---|---|
| Ajustar frecuencias a la capacidad y reducirlas si faltan proxies | Frecuencias adaptadas reservan capacidad para todos los grupos |
| Conservar prioridad por cupo, respetar espera y seleccionar activos y fondo | Mercado reserva sin duplicar y guarda historial idempotente en PostgreSQL |
| Evitar reservas simultáneas duplicadas | Cinco conexiones reservan artículos distintos sin consultar dos veces el mismo |

264/264 regresiones y 23/23 comprobaciones con PostgreSQL aprobadas. Las comprobaciones usan tablas temporales o un esquema de pruebas que se elimina al terminar; no llaman a Roblox.
