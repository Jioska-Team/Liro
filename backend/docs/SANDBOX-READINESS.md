# Evaluación previa al sandbox

Revisión de código y estructura de despliegue, 2026-10-19. No se ha instalado, reiniciado ni conectado un entorno nuevo. No equivale a una prueba de carga ni a una auditoría de seguridad completa.

## Dictamen

La solución tiene una base funcional para preparar un sandbox privado. El backend completo todavía no tiene un despliegue Staging verificado y no debe instalarse como una copia que comparta recursos activos. El bot Discord.Alerts tiene configuración y Compose independientes, pero faltan credenciales y validación real de entrega.

## Requisitos antes de arrancar el backend completo

| Área | Evidencia actual | Preparación necesaria |
|---|---|---|
| Arranque | AppHost configura PostgreSQL, Valkey, API y workers para desarrollo. `Start-Lan.ps1` activa LAN y AppHost rechaza LAN fuera de Development. | Definir despliegue de API y workers en Staging; no reutilizar el arranque LAN. |
| Datos | DatabaseInitializationService migra automáticamente solo en Development; fuera de ese ambiente rechaza migraciones pendientes. También elimina artículos ajenos según la política existente y reprograma consultas antiguas. | Base aislada, aplicar migraciones explícitamente y usar datos de prueba o copia autorizada. Nunca apuntar el sandbox a la base activa. |
| Coordinación | Valkey coordina trabajo y cooldowns, con claves compartidas por convención. | Instancia aislada de Valkey y volúmenes propios para evitar compartir colas, bloqueos o límites. |
| Seguridad y acceso | API tiene autenticación administrativa y un modo LAN exclusivo de Development. | Configurar Security__AdminApiKey, acceso privado/TLS y orígenes del frontend acordes al despliegue. No exponer públicamente como parte de esta instalación. |
| Salud | ServiceDefaults publica health/alive únicamente en Development; el diagnóstico infrastructure también se limita a Development. | Definir probes internos para Staging y comprobar reinicios supervisados y logs. |
| Proxies | Mercado usa MarketProxySecrets y cuota externa de Webshare. | Configuración explícita de secretos y presupuesto para sandbox; otra clave API en la misma cuenta no separa cuota ni capacidad de los proxies. Preferir simulaciones para pruebas sin tráfico real o un conjunto reservado con presupuesto controlado. |
| Discord | LocalControl depende de Windows/local development. Alerts es independiente y distingue ambientes. | Mantener LocalControl fuera del sandbox. Configurar bot/canal/token de alertas Staging, clave Webshare y volumen persistente propio. |
| Publicación | deploy contiene únicamente el despliegue del bot de alertas. | Preparar manifiestos del backend, procedimiento de migración y reversión y una prueba integral con recursos aislados antes de declararlo listo. |

No hay una medición actual de CPU/RAM/disco del servidor destino; no se puede certificar su capacidad con esta revisión. El estado de cuota de Webshare tampoco se volvió a consultar.
