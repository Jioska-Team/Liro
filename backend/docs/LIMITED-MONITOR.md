# Monitor de limiteds y ventanas recientes

La configuración operativa desactiva `CatalogWorkers:BootstrapEnabled` y activa
`BoundedRecentDiscovery`. `HistoricalRefreshEnabled=false` desactiva el recorrido ordinario
de artículos antiguos y sus reintentos generales. `RefreshEnabled=true` conserva la cola
prioritaria de confirmaciones. No elimina artículos, fallos ni checkpoints anteriores.
`BoundedLimitedDiscovery=false` hace que el monitor de coleccionables recorra todos los
cursores; `RecentLimitedLimit=600` queda inactivo en esta modalidad.

## Flujos

| Flujo | Alcance | Programación |
|---|---|---|
| Artículos recientes | Hasta 1.200 observaciones, creador User 1, orden de creación descendente, incluidos fuera de venta | Nueva pasada 5 minutos después de terminar |
| Coleccionables | Todas las páginas disponibles con SalesTypeFilter=2 y orden de creación descendente, incluidos fuera de venta | Nueva pasada inmediatamente después de terminar; cada página compite por presupuesto y turno. LimitedPollInterval controla la consulta de cabecera durante pasadas largas |
| IDs conocidos | Lista persistente independiente del estado actual de Items; detalles por lotes de 100 | Cada ID vuelve a estar disponible 1 minuto después de confirmarse |
| Búsquedas complementarias | 23 variantes por tipo de asset/bundle y orden, incluidos fuera de venta; una página por variante en cada turno | Nueva pasada 15 minutos después de terminar |
| Actualización ordinaria | Recorrido de artículos antiguos existentes | Desactivada; los encontrados por búsqueda y los limiteds vigilados sí se actualizan |

Los resultados completos se guardan por página/lote; la identidad es `(ItemType, RobloxAssetId)`.
Los artículos nuevos se insertan y los existentes se actualizan. Un Asset y un Bundle con el
mismo número son identidades distintas. Las ventanas no expanden componentes de bundles;
los bundles sí participan como artículos. El monitor rápido omite solicitudes de miniaturas.

`CatalogLimitedWatches` incorpora los limiteds existentes y los detectados en búsquedas.
Una vez registrado, un ID sigue vigilado aunque Items cambie a Normal, incluso con la revisión histórica desactivada. Cada lote se reserva
atómicamente durante cinco minutos; una caída deja expirar esa reserva sin perder IDs.
Las confirmaciones antiguas no pueden completar una reserva posterior. Los fallos respetan
el backoff y dead-letter de `CatalogImportFailures`; los registros fallidos se mantienen visibles.

Los IDs auditados se incorporaron mediante mantenimiento local con confirmación actual de Roblox.
La aplicación por sí sola no reconstruye una lista de IDs que nunca haya conocido.

## Tiempos y límites

Un minuto es el intervalo mínimo para volver a revisar un ID, **no una garantía de detección**.
Todas las solicitudes comparten el regulador HTTP (mínimo 3 segundos entre solicitudes), con
prioridad para el monitor y los IDs conocidos. Para 2.551 IDs hacen falta al menos 26 peticiones
de detalles por vuelta; hay que sumar latencia, otros flujos y pausas 429 de Roblox.

La ventana de recientes es un máximo de observaciones, no una promesa de 1.200 IDs únicos. Si Roblox
agota el cursor antes, se registra `HitLimit=false`. El monitor de coleccionables no tiene corte
de 600; sigue hasta agotar el cursor que Roblox entrega. SortType=3 ordena por creación del artículo,
no demuestra cuándo se convirtió en limited. SalesTypeFilter=2 también puede devolver artículos
solo Collectible; la clasificación Limited/LimitedUnique conserva la validación por restricciones.
Las búsquedas complementarias amplían cobertura, pero no prueban haber enumerado todo Roblox.
Un artículo antiguo nunca conocido que se convierta en limited depende de su descubrimiento
en búsquedas; ya no dispone del recorrido ordinario como alternativa. No existe un plazo garantizado para ese caso.

## Diagnóstico

En Workers buscar `Known limited batch completed`, `Known limited watchlist`,
`Limited scan completed`, `Recent item window completed` y
`Complementary limited pass completed`. Los registros incluyen páginas, observaciones o
duración, según el flujo. Los checkpoints `:limited-complement:v1:` conservan cada cursor.

```sql
SELECT COUNT(*) AS "Vigilados",
       COUNT(*) FILTER (WHERE "LastConfirmedAtUtc" IS NULL) AS "NuncaConfirmados",
       COUNT(*) FILTER (WHERE "NextCheckAtUtc" <= NOW()) AS "Pendientes",
       MIN("LastConfirmedAtUtc") AS "ConfirmacionMasAntigua",
       NOW() - MIN("LastConfirmedAtUtc") AS "AntiguedadMaxima"
FROM "CatalogLimitedWatches";

SELECT w."ItemType", w."ItemId", w."LastConfirmedAtUtc",
       f."Attempts", f."NextAttemptAtUtc", f."DeadLetteredAtUtc", f."Error"
FROM "CatalogLimitedWatches" w
JOIN "CatalogImportFailures" f
  ON f."ItemType" = w."ItemType" AND f."AssetId" = w."ItemId";
```

La antigüedad máxima indica la frescura de las confirmaciones, no la hora real del cambio
en Roblox. Para confirmar una vuelta completa, `NuncaConfirmados` debe ser cero y la
confirmación más antigua debe ser posterior al inicio de la observación (salvo errores visibles).

Con la revisión histórica desactivada, `Catalog queue health. Overdue` todavía puede mostrar
artículos antiguos vencidos: sus fechas se conservan, pero esa cola general no se procesa.
Las señales incompletas de coleccionables siguen atendidas por la cola prioritaria.

Para activar cambios del código/configuración en una instancia ya iniciada, reiniciar el backend
desde el botón de Discord. No hace falta reinstalar el agente para estos cambios de Workers.
