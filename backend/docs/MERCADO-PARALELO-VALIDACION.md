# Validación de mercado en paralelo

Prueba supervisada del 18 de octubre de 2026. Se activaron cinco proxies existentes, con seis solicitudes por minuto por conexión. Solo se reinició el recurso de mercado; API y catálogo conservaron sus procesos. No se cambió su presupuesto ni la recuperación histórica retirada.

| Etapa | Muestra | Guardados | Guardados/minuto | Máximo conjunto en 60 segundos | Principal, media |
|---|---:|---:|---:|---:|---:|
| Referencia secuencial | 8 minutos | 112 | 14,0 | — | 73,7 segundos |
| 18/minuto | 2,35 minutos | 39 | 16,6 | 18 | 65,4 segundos |
| 24/minuto | 2,73 minutos | 62 | 22,7 | 24 | 68,7 segundos |
| 30/minuto | 4,82 minutos | 132 | 27,4 | 30 | 77,1 segundos |

En la muestra conjunta posterior, de 10,53 minutos, hubo 249 guardados y 250 solicitudes registradas (la última aún en curso al corte). Los cinco proxies participaron; ninguno superó seis solicitudes en una ventana móvil de 60 segundos. No hubo solicitudes repetidas del mismo coleccionable dentro de un minuto. No se registraron 403, 429 ni fallos de consulta o guardado en mercado. La consulta de base inmediatamente posterior confirmó 251 artículos revisados, 57 nuevas observaciones de RAP y cero artículos con fallos acumulados; la diferencia de dos guardados corresponde a los cortes de observación distintos.

Catálogo registró un 403 recuperado en 13 segundos, sin fallo definitivo ni 429. La principal completó ocho recorridos con media de 72,1 segundos en el conjunto de las etapas. Conocidos registró 11.221 confirmaciones en 69 lotes, media de 9,2 segundos y cero fallos. Las comprobaciones de PostgreSQL no mostraron bloqueos ni interbloqueos. Estas muestras cortas no garantizan ausencia de errores futuros ni certifican una vuelta completa de mercado.

Configuración final: cinco consultas en curso como máximo, una por proxy, un guardado simultáneo, presupuesto conjunto de 30 por minuto y seis por conexión. La separación de salidas es de dos segundos; cada proxy mantiene tres segundos como mínimo entre consultas. Los presupuestos conservan su contabilidad en Redis entre reinicios y cambios de etapa. Se respetan las esperas del proveedor y un 403/429 impone espera a todo mercado, sin cambiar de proxy para eludirla.

## Pruebas ejecutadas

263/263 regresiones generales y 22/22 comprobaciones focalizadas de mercado con PostgreSQL aprobaron. Las pruebas de integración usan tablas temporales y un esquema aleatorio que eliminan al terminar; no modifican el catálogo real.

| Requisito | Evidencia |
|---|---|
| Cinco consultas, una por proxy | `Mercado limita cinco consultas y no duplica una ruta ocupada` |
| Salidas escalonadas con respuestas simultáneas y cancelación | `Mercado escalona salidas sin esperar la respuesta anterior` |
| Identificar aliases de una misma conexión | `Mercado identifica aliases de la misma dirección de proxy` |
| Reservas concurrentes de artículos distintos | `Cinco conexiones reservan artículos distintos sin consultar dos veces el mismo` |
| Guardado sin duplicados y reserva vigente | `Mercado reserva sin duplicar y guarda historial idempotente en PostgreSQL` |
| Respetar esperas del proveedor | `Respuesta 429 conserva esperas indicadas por proveedor` |

## Recomendación

Mantener cinco proxies por ahora y observar una muestra más larga antes de considerar diez. Si aparecen errores de mercado o una degradación sostenida, reducir primero el presupuesto conjunto y respetar la espera indicada por Roblox. La ampliación se valida por etapas; no se presume que cada IP tenga una cuota independiente ni se promete multiplicar linealmente la velocidad. A 27,4 revisiones por minuto, 2.554 artículos equivalen a unos 93 minutos de trabajo, sin contar revisiones repetidas o esperas futuras: es una proyección, no una vuelta completa medida.
