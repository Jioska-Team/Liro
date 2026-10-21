# Liro

Liro desarrolla una plataforma de catálogo y análisis de limiteds de Roblox. El backend mantiene un monolito modular con Clean Architecture; los clientes consumen su API.

## Estado y estructura

| Ruta | Contenido actual |
| --- | --- |
| [backend/](backend/README.md) | API, catálogo, seguimiento de mercado, servicios Discord y orquestación Aspire |
| [web/liro-web/](web/liro-web/README.md) | Angular con SSR configurado, consulta de catálogo y componentes de bundles |
| `extension/`, `roblox/` | Carpetas reservadas, sin implementación en este repositorio |
| `docs/` | Diseño compartido y manuales de referencia |
| `.codex/`, `.omx/` | Configuración, instrucciones y estado de herramientas; no documentación funcional |

Catálogo, bundles, observaciones de precios, series de RAP/ventas y Outbox tienen implementación. Trading, portfolios, valoración y SignalR siguen siendo diseño previsto; no se deben inferir a partir de dependencias instaladas.

## Navegación y fuentes de verdad

- [Backend: ejecución y operación](backend/README.md).
- [Índice técnico del backend](backend/docs/INDEX.md).
- [Proyectos y responsabilidades](backend/docs/SOLUTION-STRUCTURE.md), contrastados con [Liro.slnx](backend/Liro.slnx).
- [Contratos HTTP para frontend](backend/docs/frontend/README.md).
- [Desarrollo web](web/liro-web/README.md).
- [Arquitectura y patrones acordados](docs/LIRO_ARQUITECTURA_Y_PATRONES.md) y [diagramas de diseño](docs/LIRO_DIAGRAMA_FLUJO.md).
- [Manual del desarrollador](docs/LIRO_MANUAL_DESARROLLADOR.md) y [manual de instalación original](docs/LIRO_MANUAL_INSTALACION.md).

El código y la configuración versionada describen lo implementado; los documentos de diseño preservan decisiones y objetivos, sin probar su implementación. Las versiones se consultan en los `.csproj`, [package.json](web/liro-web/package.json) y [package-lock.json](web/liro-web/package-lock.json). Actualmente se declara .NET 10, Aspire 13.6.1 y Angular 22.2.x. AppHost no fija un tag explícito para las imágenes de infraestructura.

## Instrucciones para agentes

`AGENTS.md` en la raíz es el contrato local generado por OMX y está excluido de Git. [backend/AGENTS.md](backend/AGENTS.md) añade las reglas del backend. No editar automáticamente los archivos administrados ni sus marcadores: `.omx/setup-scope.json` registra ámbito de proyecto y `mergeAgents: true`; las actualizaciones de OMX pueden regenerar secciones.

Aplicar KISS, DRY y YAGNI, reutilizar lo existente, respetar las capas y limitar cada cambio a la tarea. Consultar solo los documentos relevantes y verificar resultados antes de declarar una tarea completa. Las tareas simples no requieren múltiples agentes.
