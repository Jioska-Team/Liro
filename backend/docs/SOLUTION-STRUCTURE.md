# Estructura de la solución y arquitectura

> Actualización 2026-10-19: el bot instalado ya usa `scripts/development`. Se retiraron los siete enlaces `.ps1` de la raíz después de verificar su conexión. Usar exclusivamente las rutas bajo `scripts/`. Las menciones anteriores a enlaces compatibles describen el estado previo.


## Contrato original

La fuente oficial es [LIRO_ARQUITECTURA_Y_PATRONES.md](../../docs/LIRO_ARQUITECTURA_Y_PATRONES.md), especialmente las secciones 3, 6, 17 y 56. Se definió un monolito modular con Clean Architecture y comunicación orientada a eventos, DDD ligero y CQRS ligero. Esto no prescribe microservicios por módulo.

La estructura inicial del repositorio, documentada en [el manual del desarrollador](../../docs/LIRO_MANUAL_DESARROLLADOR.md), separaba `backend/`, `web/`, `extension/`, `roblox/` y `docs/`. Dentro de backend: `src/` para proyectos y `tests/` para pruebas. Las capas se organizan por responsabilidad y, dentro de ellas, por módulos de negocio: Catalog, MarketData, Valuation, Trading, Portfolio, Identity y Notifications. Los módulos previstos no equivalen a funcionalidades ya implementadas.

Patrones acordados: eventos de dominio y Outbox; Ports & Adapters y normalización de Roblox; repositorios específicos y Unit of Work de EF Core; DI y DTO; Cache-Aside con Valkey; idempotencia, rate limiting, retry/backoff/circuit breaker; Strategy para algoritmos y Notification Provider/Pub-Sub para notificaciones. Son guías de uso, no una afirmación de que todos estén implementados.

## Navegación actual en Visual Studio

```text
src
├── Backend
│   ├── Liro.Api
│   ├── Liro.Application
│   ├── Liro.Domain
│   └── Liro.Infrastructure
├── Workers
│   ├── Liro.CatalogWorker
│   └── Liro.MarketWorker
├── Discord
│   ├── Liro.Discord.LocalControl
│   └── Liro.Discord.Alerts
└── Hosting
    ├── Liro.AppHost
    ├── Liro.SandboxMigrator
    └── Liro.ServiceDefaults
tests
├── Liro.RegressionTests
├── Liro.AppHost.RegressionTests
├── Liro.Discord.LocalControl.RegressionTests
└── Liro.Discord.Alerts.RegressionTests
```

Backend/Workers/Discord/Hosting son carpetas de solución en Liro.slnx. Las rutas físicas siguen siendo `backend/src/<proyecto>`. `Liro.CatalogWorker` sí sustituye la carpeta/proyecto anterior `Liro.Workers`, incluyendo namespaces, referencias del compilador, pruebas y referencia generada de Aspire.

| Proyecto | Responsabilidad |
|---|---|
| Liro.SandboxMigrator | Herramienta de despliegue en `tools/`: aplica migraciones únicamente a la base aislada del sandbox. No se ejecuta desde AppHost. |
| Liro.Domain | Entidades, reglas y eventos del negocio; independiente de infraestructura. |
| Liro.Application | Casos de uso y contratos; depende de Domain. |
| Liro.Infrastructure | EF/PostgreSQL, Valkey, clientes y adaptadores; implementa contratos internos. |
| Liro.Api | Entrada HTTP para clientes; compone Application e Infrastructure. |
| Liro.CatalogWorker | Host de descubrimiento, principal/conocidos, actualización de catálogo, componentes de bundles y procesamiento Outbox existentes. |
| Liro.MarketWorker | Host independiente de consultas RAP/historial, con cola y presupuestos propios. |
| Liro.Discord.LocalControl | Control local desde Discord: iniciar/reiniciar/stop y estado. |
| Liro.Discord.Alerts | Host independiente de alertas de consumo para Staging y Production. |
| Liro.AppHost | Orquestación de desarrollo mediante Aspire. |
| Liro.ServiceDefaults | Observabilidad, salud y configuración compartida de servicios. |

Los ejecutables actúan como puntos de composición. No se introducen referencias desde Domain hacia ejecutables ni desde Application hacia Infrastructure. El renombrado conserva el grafo de dependencias existente.

## Compatibilidad operativa del renombrado

- Se conserva el nombre de recurso Aspire `workers`: lo usan diagnósticos y operación del backend. En código el tipo generado es `Projects.Liro_CatalogWorker`.
- Se conserva el UserSecretsId antiguo para no perder configuración ya guardada. No es un proyecto pendiente de renombrar.
- Se conservan `CatalogWorkers`, claves de Valkey, checkpoints, presupuestos, DI y todas las tareas registradas.
- Los binarios y appsettings del directorio anterior se mantienen mientras el proceso existente los usa. No se deben borrar con el backend encendido. El directorio anterior ya no contiene un proyecto ni fuentes compilables.
- Los namespaces y categorías de log del catálogo pasan a `Liro.CatalogWorker.*` al arrancar el nuevo binario; adaptar consumidores externos que filtren por el prefijo antiguo. Las herramientas de diagnóstico del repositorio no contienen ese filtro antiguo.
- No se reinician procesos ni se aplican migraciones. El nuevo nombre del ensamblado entra en funcionamiento en el siguiente arranque normal que compile AppHost. Un arranque con `--no-build` sobre artefactos antiguos no actualiza referencias.

## Alcance pendiente

Organizar y renombrar no completa una migración arquitectónica. MarketStore/MarketTransport aún están en MarketWorker. Las alertas se separaron posteriormente en Liro.Discord.Alerts, con política en Application y adaptadores en Infrastructure. Ver [discord-alerts.md](discord-alerts.md). Su activación requiere configurar el nuevo bot y su despliegue.

## LocalControl: compatibilidad de instalación

El proyecto, carpeta, namespaces y pruebas se llaman Liro.Discord.LocalControl. El script principal es Setup-DiscordLocalControl.ps1; Setup-DevelopmentAgent.ps1 se conserva como enlace compatible.

Se mantiene explícitamente AssemblyName=Liro.DevelopmentAgent para que el ejecutable publicado siga coincidiendo con la tarea de Windows existente. También se conservan el nombre de tarea Liro.DevelopmentAgent, el directorio %LOCALAPPDATA%\Liro\DevelopmentAgent, el cifrado DPAPI y el bloqueo agent.lock. No hay una segunda instalación ni un segundo proceso por el cambio de nombre.

No se reinstala ni reinicia el bot al renombrar el código. Los binarios anteriores en bin/obj se conservan; no son proyectos activos. La estructura y las referencias se verifican mediante compilación en una carpeta separada.
