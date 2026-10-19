# Clasificación de mercado desde el historial guardado

La clasificación usa exclusivamente los puntos de volumen `marketplace-sales` guardados, dentro de los siete días UTC completos anteriores a la observación. Una semana parcial demuestra un mínimo de ventas: 700 o más permiten prioridad MuyAlta, 70 Alta y 7 Media. `DailySales` solo contiene un promedio cuando existen los siete puntos diarios completos; los huecos no se convierten en ventas cero.

Se distinguen InciertaReciente (puntos recientes insuficientes para demostrar la categoría media), SinVentasRecientes (existe volumen histórico pero no puntos de esa semana) y SinHistorial (sin volumen almacenado). Ninguna de las dos últimas categorías demuestra inactividad. Los siete ceros explícitos sí permiten Inactiva.

Al iniciar mercado y cada cinco minutos se revisa la evidencia almacenada. Los cambios se escriben en grupos de hasta 120, con una pausa breve entre grupos y solo si cambió la clasificación o el promedio. No se modifican filas con reservas vigentes ni observaciones que cambiaron desde la lectura. La actualización no borra las esperas del proveedor. Cada respuesta guardada vuelve a clasificar usando el conjunto del historial local, no solamente los puntos de esa respuesta.

Los activos conservan tres de cada cuatro selecciones preferentes. Los casos inciertos participan en el cuarto turno con intervalos propios más cortos que los artículos sin puntos recientes. Los intervalos se ajustan al presupuesto disponible; son objetivos, no garantías de actualización puntual. Los controles de solicitudes siguen siendo 30 por 60 segundos en mercado y 6 por proxy. No se cambia la principal ni conocidos.

## Validación

| Requisito | Evidencia |
|---|---|
| Clasificar semanas incompletas sin inventar promedios ni inactividad | Clasificación parcial prueba actividad sin inventar ventas ni promedios |
| Incluir inciertos sin superar el presupuesto planificado | Plan incluye inciertos y mantiene el presupuesto con historial parcial |
| Reclasificar desde PostgreSQL, evitar escrituras repetidas y conservar datos ante respuestas parciales | Mercado reserva sin duplicar y guarda historial idempotente en PostgreSQL |

266/266 regresiones y 25/25 comprobaciones de mercado con PostgreSQL aprobadas. Las comprobaciones no consultan Roblox. No requiere migración.

## Resultado al activar

Se reclasificaron los 2.332 registros antes marcados SinDatos: 3 pasaron a Alta, 248 a Media, 906 a InciertaReciente, 1.118 a SinVentasRecientes y 57 a SinHistorial. Totales: 9 MuyAlta, 84 Alta, 380 Media y los tres grupos anteriores. Chocolate Egg, Agonizingly Ugly Egg of Screensplat y POW! To the Moon! Egg ya se consultaron con prioridad Alta y quedaron programados a seis minutos.

Con esta población, objetivos de programación: MuyAlta 2 minutos, Alta 6, Media e InciertaReciente 236, SinHistorial 118, SinVentasRecientes 1.411. La ausencia de puntos recientes no equivale a ausencia demostrada de ventas. El mayor espaciamiento del último grupo es una decisión de reparto de capacidad, no una conclusión sobre su demanda. El plan se recalcula; las frecuencias reales requieren una muestra más larga.
