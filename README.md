# LIRO — Guía técnica esencial

## 1. Qué es Liro

Liro es una plataforma de trading y análisis de Limiteds de Roblox.

La plataforma tendrá varios clientes que consumirán un mismo backend:

- Web
- Extensión de navegador
- Juego de Roblox
- Bot de Discord
- Mobile/PWA
- Futuros clientes o integraciones

La regla principal es:

> Toda la lógica de mercado, precios, valoración y trading vive en el backend. Los clientes solo consumen Liro.Api.

---

# 2. Arquitectura

Usamos:

- **Modular Monolith**: un backend modular, fácil de mantener y preparado para crecer.
- **Clean Architecture**: separa negocio, aplicación e infraestructura.
- **Event-Driven**: los módulos reaccionan a eventos sin quedar fuertemente acoplados.
- **DDD ligero**: cada área importante del negocio se mantiene separada.
- **CQRS ligero**: separar lecturas de operaciones de escritura cuando sea útil.

Arquitectura general:

```text
Roblox APIs
    ↓
Liro.Workers
    ↓
Liro Core
    ↓
PostgreSQL + Valkey
    ↓
Liro.Api
    ↓
Web / Extension / Roblox / Discord / Mobile / futuros clientes
```

---

# 3. Backend

## Tecnologías

- .NET 10
- ASP.NET Core
- C#
- Entity Framework Core
- PostgreSQL 18
- Valkey
- SignalR
- .NET Aspire
- Docker
- OpenTelemetry

## Por qué

### .NET / ASP.NET Core

Se usa para:

- API
- lógica de negocio
- Workers
- realtime
- integraciones
- autenticación
- procesos de mercado

Se eligió por rendimiento, tipado fuerte, soporte para aplicaciones grandes y buena integración con servicios en segundo plano.

### PostgreSQL

Se usa como base de datos persistente para:

- items
- usuarios
- histórico
- trades
- portfolios
- market snapshots
- configuraciones

### Valkey

Se usa para:

- cache
- precios actuales
- RAP actual
- rankings
- trending
- datos temporales
- estado de mercado de acceso rápido

### SignalR

Se usa para enviar cambios en tiempo real desde Liro.Api hacia clientes como la web.

### .NET Aspire

Se usa para orquestar el entorno local:

```text
Liro.Api
Liro.Workers
PostgreSQL
Valkey
```

### Docker

Se usa para ejecutar infraestructura local de forma reproducible.

---

# 4. Proyectos del backend

```text
Liro.Api
Liro.Workers
Liro.Domain
Liro.Application
Liro.Infrastructure
Liro.ServiceDefaults
Liro.AppHost
```

## Liro.Domain

Contiene:

- entidades
- reglas de negocio
- value objects
- domain events

No depende de infraestructura.

## Liro.Application

Contiene:

- casos de uso
- commands
- queries
- interfaces
- contratos internos

Depende de `Liro.Domain`.

## Liro.Infrastructure

Contiene:

- Entity Framework Core
- PostgreSQL
- Valkey
- clientes HTTP
- integración con Roblox
- implementaciones técnicas

## Liro.Api

Expone:

- REST
- SignalR
- autenticación
- autorización
- OpenAPI
- versionado de API

Es la entrada principal para todos los clientes.

## Liro.Workers

Procesa tareas en segundo plano:

- descubrimiento de items
- actualización de mercado
- sincronización de inventarios
- procesamiento periódico

## Liro.ServiceDefaults

Configuración compartida:

- OpenTelemetry
- Health Checks
- Service Discovery
- Resilience

## Liro.AppHost

Orquesta el entorno local con Aspire.

---

# 5. Dependencias del backend

```text
Domain
  ↑
Application
  ↑
Infrastructure
  ↑
Api / Workers

AppHost
  ├── Api
  └── Workers
```

Reglas:

- Domain no depende de otros proyectos.
- Application depende de Domain.
- Infrastructure depende de Application y Domain.
- Api y Workers consumen Application e Infrastructure.
- AppHost solo orquesta.

---

# 6. Frontend Web

## Tecnologías

- Angular 22
- TypeScript
- CSS
- Angular Signals
- Angular SSR
- SignalR Client
- Lightweight Charts

## Por qué

### Angular

Se usa para la aplicación web principal porque ofrece:

- estructura clara
- routing
- componentes
- tipado con TypeScript
- buen soporte para aplicaciones grandes

### SSR

Se usa porque Liro tendrá páginas públicas de items y mercado que deben ser visibles correctamente para buscadores y previews.

### Angular Signals

Se usan para estado reactivo y actualización de UI.

### SignalR Client

Se usa para recibir cambios de mercado en tiempo real.

### Lightweight Charts

Se usa para gráficos de precios y mercado.

---

# 7. Estructura del frontend

```text
src/app/
├── core/
│   ├── api/
│   ├── auth/
│   ├── realtime/
│   ├── guards/
│   ├── interceptors/
│   └── config/
│
├── shared/
│   ├── components/
│   ├── directives/
│   ├── pipes/
│   └── utils/
│
├── layout/
│   ├── header/
│   ├── sidebar/
│   ├── footer/
│   └── mobile-navigation/
│
└── features/
    ├── market/
    ├── items/
    ├── trading/
    ├── portfolio/
    ├── profiles/
    ├── watchlist/
    ├── alerts/
    └── authentication/
```

Regla:

```text
Component
   ↓
Service
   ↓
Liro.Api
```

Los componentes no deben llamar directamente al backend.

---

# 8. Extensión de navegador

## Tecnologías

- TypeScript
- Manifest V3
- REST hacia Liro.Api
- SignalR cuando sea necesario

## Uso

- analizar trades
- mostrar projected
- mostrar ganancias/pérdidas
- mostrar demanda, liquidez y valor
- consultar información de items

La extensión no calcula precios por su cuenta.

---

# 9. Roblox

## Tecnologías

- Roblox Studio
- Luau
- HttpService
- Open Cloud
- MessagingService

## Uso

Roblox consume Liro.Api para obtener datos.

Modelo:

```text
REST
→ snapshot / estado completo

Messaging
→ avisos rápidos
```

---

# 10. Discord

## Tecnologías

- Bot de Discord
- Liro.Api
- sistema de eventos/notificaciones

## Uso

Comandos:

```text
Discord
  ↓
Liro.Api
```

Notificaciones:

```text
Liro Event
  ↓
Notifications
  ↓
Discord
```

Ejemplos:

- nuevos Limiteds
- cambios de RAP
- proyectados
- cambios de value
- ventas grandes

---

# 11. Mobile

Primero:

- Angular responsive
- PWA

Si más adelante se necesita aplicación nativa:

- Capacitor

La app seguirá consumiendo Liro.Api.

---

# 12. Patrones de diseño

## Adapter / Ports & Adapters

Se usa para aislar Roblox y otros servicios externos.

```text
Application
   ↓
IRobloxProvider
   ↓
Roblox Adapter
```

## Strategy

Se usa para algoritmos que pueden cambiar:

- Liro Value
- Projected
- Demand
- Liquidity

## Domain Events

Se usan para representar hechos importantes:

```text
ItemDiscovered
ItemBecameLimited
RapChanged
ProjectedDetected
ValueChanged
LargeSaleDetected
```

## Outbox

Se usará cuando sea necesario garantizar que eventos importantes no se pierdan.

## Cache-Aside

```text
Valkey
  ↓
si no existe
  ↓
PostgreSQL
```

## Background Worker

Se usa para procesos que deben ejecutarse aunque no haya usuarios conectados.

## Retry + Exponential Backoff

Se usa para llamadas externas que pueden fallar temporalmente.

## Circuit Breaker

Evita golpear continuamente un servicio externo cuando está fallando.

## Rate Limiting

Controla el número de llamadas hacia APIs externas.

## Idempotency

Evita duplicar información cuando el mismo dato se procesa varias veces.

## Unit of Work

Se usa mediante `DbContext` de Entity Framework Core.

## Repository específico

Solo se usa cuando una consulta de negocio compleja realmente lo necesita.

No se usará un `GenericRepository<T>` para todo.

## Observer / Pub-Sub

Se usa para realtime y notificaciones.

## DTO

Los contratos públicos de API se mantienen separados de las entidades internas.

## Dependency Injection

Permite sustituir implementaciones y facilita pruebas.

## API Versioning

Los endpoints públicos deberán versionarse:

```text
/api/v1/...
```

---

# 13. Software necesario

## General

- Git
- Visual Studio
- Visual Studio Code

## Backend

- .NET 10 SDK
- Docker Desktop
- WSL 2
- .NET Aspire CLI

## Frontend

- Node.js 24 LTS
- npm
- Angular CLI 22

## Roblox

- Roblox Studio

## Extensión

- Chrome o navegador Chromium

## Opcional

- DBeaver para PostgreSQL

---

# 14. Levantar el proyecto

## 1. Iniciar Docker

Comprobar:

```powershell
docker info
```

Debe mostrar información de Client y Server.

## 2. Levantar backend

Abrir la solución:

```text
backend/Liro.slnx
```

Ejecutar:

```text
Liro.AppHost
```

desde Visual Studio.

Aspire debe mostrar:

```text
postgres   Running
lirodb     Running
valkey     Running
api        Running
workers    Running
```

## 3. Levantar frontend

Desde:

```text
web/liro-web
```

ejecutar:

```powershell
npm ci
ng serve --proxy-config proxy.conf.json
```

Abrir:

```text
http://localhost:4200
```

---

# 15. Proxy local del frontend

Ejemplo:

```json
{
  "/api": {
    "target": "https://localhost:7000",
    "secure": false,
    "changeOrigin": true
  }
}
```

La URL es solo un ejemplo de desarrollo.

Angular debe consumir:

```text
/api/...
```

en lugar de guardar URLs completas por todo el código.

---

# 16. Comprobaciones básicas

Backend:

```powershell
dotnet build
```

Frontend:

```powershell
npm ci
ng serve --proxy-config proxy.conf.json
```

Docker:

```powershell
docker info
docker ps
```

---

# 17. Lista resumida por proyecto

| Proyecto | Tecnologías principales |
|---|---|
| Backend | .NET 10, ASP.NET Core, EF Core |
| API | REST, SignalR, OpenAPI |
| Workers | .NET Worker Services |
| Base de datos | PostgreSQL |
| Cache | Valkey |
| Orquestación local | .NET Aspire |
| Contenedores | Docker |
| Observabilidad | OpenTelemetry |
| Web | Angular 22, TypeScript, CSS, SSR, Signals |
| Gráficos | Lightweight Charts |
| Extensión | TypeScript, Manifest V3 |
| Roblox | Luau, HttpService, Open Cloud, MessagingService |
| Discord | Bot + Liro.Api + Notifications |
| Mobile | Angular responsive/PWA, Capacitor si se necesita |
| Versionado | Git |

---

# 18. Regla final

```text
Liro Core
→ contiene el negocio

Liro.Infrastructure
→ contiene detalles técnicos

Liro.Workers
→ mantienen datos actualizados

Liro.Api
→ expone la plataforma

Web / Extension / Roblox / Discord / Mobile
→ consumen Liro.Api
```

Todos los clientes deben compartir la misma fuente de datos y lógica de negocio.
