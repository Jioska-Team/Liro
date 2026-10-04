# LIRO — Guía para levantar el proyecto

## Objetivo

Este documento resume lo necesario para que cualquier desarrollador pueda levantar **Liro** en local sin reconstruir la configuración desde cero.

Actualmente Liro usa:

- .NET 10
- ASP.NET Core
- .NET Aspire
- PostgreSQL
- Valkey
- Docker Desktop
- WSL 2
- Angular 22
- Angular SSR
- SignalR
- Entity Framework Core
- OpenTelemetry / Service Defaults

---

# 1. Requisitos instalados

El desarrollador debe tener:

- Visual Studio
- .NET 10 SDK
- Visual Studio Code
- Node.js 24 LTS
- npm
- Angular CLI 22
- Docker Desktop
- WSL 2
- Git
- Roblox Studio, si trabajará la parte Roblox
- Chrome, si trabajará la extensión

Comprobar:

```powershell
dotnet --version
node --version
npm --version
ng version
docker --version
docker compose version
wsl --version
git --version
```

---

# 2. Estructura actual

```text
D:\Proyectos\Liro
│
├── backend
│   ├── Liro.slnx
│   └── src
│       ├── Liro.Api
│       ├── Liro.AppHost
│       ├── Liro.Application
│       ├── Liro.Domain
│       ├── Liro.Infrastructure
│       ├── Liro.ServiceDefaults
│       └── Liro.Workers
│
├── web
│   └── liro-web
│
├── extension
├── roblox
└── docs
```

---

# 3. Arquitectura del backend

```text
Domain
  ▲
  │
Application
  ▲
  │
Infrastructure
  ▲        ▲
  │        │
 API    Workers
  ▲        ▲
  └── AppHost ──┘
```

Reglas:

```text
Liro.Domain
→ no depende de otros proyectos.

Liro.Application
→ depende de Domain.

Liro.Infrastructure
→ depende de Application + Domain.

Liro.Api
→ depende de Application + Infrastructure + ServiceDefaults.

Liro.Workers
→ depende de Application + Infrastructure + ServiceDefaults.

Liro.AppHost
→ orquesta Api, Workers, PostgreSQL y Valkey.
```

---

# 4. Levantar Docker

Antes de iniciar Liro, Docker Desktop debe estar funcionando.

Comprobar:

```powershell
docker info
```

Debe aparecer información de:

```text
Client
Server
```

Si solo aparece Client y falla Server, Docker Engine no está levantado.

---

# 5. Problema conocido de Docker en Windows

En este equipo ocurrió que Docker no podía iniciar porque:

```text
vmcompute
```

estaba detenido.

Comprobar:

```powershell
Get-Service hns
Get-Service vmcompute
```

Los dos deberían estar:

```text
Running
```

Si `vmcompute` está detenido:

```powershell
Start-Service vmcompute
```

También puede ser necesario:

```powershell
Start-Service hns
```

Después:

```powershell
docker info
```

---

# 6. Requisitos de virtualización

Comprobar WSL:

```powershell
wsl --status
wsl --version
```

La versión predeterminada debe ser:

```text
2
```

Características de Windows:

```powershell
Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux
Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform
```

Ambas deben indicar:

```text
State : Enabled
```

Comprobar hipervisor:

```powershell
bcdedit.exe /enum "{current}"
```

Debe aparecer:

```text
hypervisorlaunchtype    Auto
```

---

# 7. Levantar el backend

Abrir:

```text
D:\Proyectos\Liro\backend\Liro.slnx
```

en Visual Studio.

Seleccionar:

```text
Liro.AppHost
```

como proyecto de inicio y ejecutar:

```text
F5
```

Aspire debe levantar:

```text
postgres
lirodb
valkey
api
workers
```

Todos deberían aparecer como:

```text
Running
```

---

# 8. Aspire Dashboard

Al levantar `Liro.AppHost`, Aspire abre un dashboard local.

Desde ahí se pueden consultar:

- logs
- endpoints
- traces
- estado de recursos
- health
- URLs de la API

Ejemplo:

```text
postgres   Running
lirodb     Running
valkey     Running
api        Running
workers    Running
```

---

# 9. PostgreSQL

PostgreSQL NO se instala directamente en Windows.

Se ejecuta en Docker mediante Aspire.

Versión actual:

```text
PostgreSQL 18
```

Base actual:

```text
lirodb
```

---

# 10. Valkey

Valkey también se ejecuta mediante Docker + Aspire.

Uso previsto:

- cache
- RAP actual
- estado actual de mercado
- rankings
- projected
- trending
- datos temporales

---

# 11. Infrastructure

`Liro.Infrastructure` registra PostgreSQL y Valkey.

Archivo:

```text
Liro.Infrastructure/DependencyInjection.cs
```

Configuración:

```csharp
builder.AddNpgsqlDbContext<LiroDbContext>("lirodb");
builder.AddRedisClient("valkey");
```

Las conexiones llegan desde Aspire.

No escribir connection strings manuales en código.

---

# 12. LiroDbContext

Ubicación:

```text
Liro.Infrastructure/Persistence/LiroDbContext.cs
```

Base actual:

```csharp
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

public sealed class LiroDbContext(
    DbContextOptions<LiroDbContext> options)
    : DbContext(options)
{
}
```

---

# 13. Liro.Api

`Liro.Api` será la puerta de entrada para:

- Angular
- extensión
- Roblox
- Discord
- Mobile
- futuros clientes

Debe registrar:

```csharp
builder.AddServiceDefaults();
builder.AddInfrastructure();
```

y:

```csharp
app.MapDefaultEndpoints();
```

---

# 14. Endpoint de infraestructura

Endpoint temporal:

```text
GET /api/infrastructure
```

Ejemplo:

```text
https://localhost:7174/api/infrastructure
```

Respuesta esperada:

```json
{
  "postgres": "ok",
  "valkey": "ok",
  "valkeyLatencyMs": 1.17
}
```

Esto confirma:

```text
Liro.Api
   ├── PostgreSQL ✅
   └── Valkey ✅
```

---

# 15. 404 en `/`

Esto es normal:

```text
https://localhost:7174/
```

puede devolver:

```text
404 Not Found
```

porque todavía no existe endpoint raíz.

---

# 16. OpenAPI

En desarrollo:

```text
https://localhost:7174/openapi/v1.json
```

---

# 17. Liro.ServiceDefaults

Debe crearse con la plantilla oficial de Aspire.

No usar una Class Library normal.

Plantilla:

```powershell
dotnet new aspire-servicedefaults
```

Incluye:

- AddServiceDefaults()
- MapDefaultEndpoints()
- OpenTelemetry
- Health Checks
- Service Discovery
- Resilience

---

# 18. Levantar Angular

Ubicación:

```text
D:\Proyectos\Liro\web\liro-web
```

Instalar dependencias:

```powershell
npm ci
```

Después:

```powershell
ng serve --proxy-config proxy.conf.json
```

Abrir:

```text
http://localhost:4200
```

---

# 19. Proxy Angular → Liro.Api

Archivo:

```text
proxy.conf.json
```

Configuración:

```json
{
  "/api": {
    "target": "https://localhost:7174",
    "secure": false,
    "changeOrigin": true
  }
}
```

Angular puede consumir:

```text
/api/infrastructure
```

sin repetir la URL completa del backend.

---

# 20. HttpClient

En:

```text
src/app/app.config.ts
```

debe existir:

```typescript
provideHttpClient()
```

---

# 21. Angular 22 zoneless

No añadir:

```typescript
provideZoneChangeDetection(...)
```

La UI se actualizará con:

```text
Angular Signals
```

y los datos realtime llegarán con:

```text
SignalR
```

Flujo:

```text
SignalR
   ↓
Signal
   ↓
Angular actualiza la UI
```

---

# 22. Dependencias frontend

Instaladas:

```powershell
npm install @microsoft/signalr lightweight-charts
```

Uso:

```text
@microsoft/signalr
→ realtime

lightweight-charts
→ gráficos de mercado
```

---

# 23. Estructura frontend

```text
src/app/
│
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

---

# 24. Regla frontend

No hacer HTTP directamente desde componentes.

Usar:

```text
Component
   ↓
Service
   ↓
Liro.Api
```

---

# 25. InfrastructureService

Ubicación actual:

```text
src/app/core/api/infrastructure.ts
```

Import correcto:

```typescript
import { InfrastructureService } from './core/api/infrastructure';
```

No usar:

```typescript
./core/api/infrastructure.service
```

porque el archivo generado actualmente se llama:

```text
infrastructure.ts
```

---

# 26. Orden correcto para levantar todo

## Paso 1

Abrir Docker Desktop.

Verificar:

```powershell
docker info
```

---

## Paso 2

Abrir Visual Studio.

Abrir:

```text
backend/Liro.slnx
```

Ejecutar:

```text
Liro.AppHost
F5
```

Esperar:

```text
postgres   Running
lirodb     Running
valkey     Running
api        Running
workers    Running
```

---

## Paso 3

Abrir terminal:

```powershell
cd D:\Proyectos\Liro\web\liro-web
```

Ejecutar:

```powershell
ng serve --proxy-config proxy.conf.json
```

---

## Paso 4

Abrir:

```text
http://localhost:4200
```

---

# 27. Flujo local completo

```text
Docker Desktop
     ↓
Visual Studio
     ↓
Liro.AppHost
     ↓
Aspire
     │
     ├── PostgreSQL
     ├── Valkey
     ├── Liro.Api
     └── Liro.Workers

Angular
     ↓
localhost:4200
     ↓
proxy.conf.json
     ↓
Liro.Api
     ↓
PostgreSQL / Valkey
```

---

# 28. Comandos útiles

## Backend

```powershell
cd D:\Proyectos\Liro\backend
dotnet build .\Liro.slnx
```

## Frontend

```powershell
cd D:\Proyectos\Liro\web\liro-web
npm ci
ng serve --proxy-config proxy.conf.json
```

## Docker

```powershell
docker info
docker ps
```

## Servicios Windows

```powershell
Get-Service hns
Get-Service vmcompute
```

## WSL

```powershell
wsl --status
wsl --version
```

---

# 29. Troubleshooting

## Docker no arranca

```powershell
Get-Service vmcompute
```

Si está detenido:

```powershell
Start-Service vmcompute
```

Después:

```powershell
docker info
```

---

## Angular no encuentra un servicio

Comprobar nombre físico del archivo.

Actual:

```text
infrastructure.ts
```

Import:

```typescript
./core/api/infrastructure
```

---

## Angular muestra NG0908 / Zone.js

Eliminar:

```typescript
provideZoneChangeDetection(...)
```

---

## Angular no llega al backend

Confirmar:

```text
proxy.conf.json
```

y arrancar con:

```powershell
ng serve --proxy-config proxy.conf.json
```

También confirmar:

```text
api Running
```

en Aspire.

---

## PostgreSQL o Valkey no responden

Abrir:

```text
/api/infrastructure
```

Respuesta correcta:

```json
{
  "postgres": "ok",
  "valkey": "ok"
}
```

---

# 30. Resumen de onboarding

Después de tener el entorno instalado:

```text
1. git clone
2. npm ci
3. iniciar Docker Desktop
4. abrir Liro.slnx
5. F5 en Liro.AppHost
6. esperar todos los recursos Running
7. ng serve --proxy-config proxy.conf.json
8. abrir localhost:4200
```

---

# 31. Estado actual

```text
Backend
✅ .NET 10
✅ ASP.NET Core
✅ Aspire
✅ Liro.Api
✅ Liro.Workers
✅ Liro.Infrastructure
✅ Liro.Domain
✅ Liro.Application
✅ Liro.ServiceDefaults

Data
✅ PostgreSQL
✅ Valkey

Infra
✅ Docker
✅ WSL 2
✅ OpenTelemetry base
✅ Health Checks

Frontend
✅ Angular 22
✅ SSR
✅ HttpClient
✅ Proxy a Liro.Api
✅ SignalR instalado
✅ Lightweight Charts instalado
```

---

# 32. Próximos pasos

Backend:

```text
Catalog
→ Items
→ MarketData
→ Valuation
→ Trading
```

Frontend:

```text
Market
Items
Trading
Portfolio
Profiles
Watchlist
Alerts
```

---

**Proyecto:** Liro  
**Documento:** Guía para levantar el proyecto  
**Uso:** onboarding / desarrollo local  
**Estado:** Base actual
