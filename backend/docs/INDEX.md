# Documentación del backend

Punto de entrada: [ejecución y operación](../README.md). La estructura real está en
[Liro.slnx](../Liro.slnx) y sus proyectos; las opciones y registros de servicios
determinan el comportamiento vigente, no las cifras de una prueba histórica.

| Área | Documentación |
| --- | --- |
| Arquitectura | [Solución y responsabilidades](SOLUTION-STRUCTURE.md) |
| Contratos HTTP | [Frontend](frontend/README.md), [rutas y límites](api-route-conventions.md), [series RAP/ventas](market-history-api.md) |
| Catálogo | [Descubrimiento](CATALOG-DISCOVERY.md), [descubrimiento adicional](CATALOG-ADDITIONAL-DISCOVERY.md), [bundles y componentes](catalogo-paquetes-componentes.md) |
| Limiteds y solicitudes | [Monitor](LIMITED-MONITOR.md), [prioridades](CATALOG-REFRESH-PRIORITY.md), [presupuesto y reutilización](optimizacion-solicitudes-roblox.md), [403](DIAGNOSTICO-403-CATALOGO.md) |
| Mercado | [Seguimiento](MERCADO-SEGUIMIENTO.md), [respaldo directo y pausas compartidas](../MARKET-DIRECT-FALLBACK.md), [cola](MERCADO-COLA-PRIORIDAD.md), [clasificación](MERCADO-CLASIFICACION-HISTORIAL.md) |
| Discord | [Instalación local](../DISCORD-DEVELOPMENT.md), [control](DISCORD-BACKEND-CONTROL.md), [servicio de alertas](discord-alerts.md), [consumo Webshare](webshare-bandwidth-alerts.md) |
| Operación | [LAN](../LAN-DEVELOPMENT.md), [scripts](../scripts/README.md), [respaldos](../scripts/maintenance/README.md) |
| Contexto histórico | [Validación paralela de mercado](MERCADO-PARALELO-VALIDACION.md), [recuperación histórica retirada](RECUPERACION-HISTORICA.md) |

Los informes históricos preservan evidencia de su fecha; no certifican el despliegue actual.
Los diseños compartidos permanecen en [docs de la raíz](../../README.md); no trasladar aquí
ni a la raíz la documentación especializada solo por uniformidad.
