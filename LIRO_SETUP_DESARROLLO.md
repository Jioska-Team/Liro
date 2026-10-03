# LIRO — Entorno de desarrollo base

> Documento inicial para preparar una PC Windows desde cero para desarrollar **Liro**.
>
> Stack base acordado:
>
> - Backend: **ASP.NET Core sobre .NET 10 LTS**
> - Frontend web: **Angular 22**
> - Base de datos: **PostgreSQL 18**
> - Cache / datos calientes: **Valkey**
> - Tiempo real web: **SignalR**
> - Workers: **.NET Worker Services**
> - Extensión: **TypeScript + Manifest V3**
> - Roblox: **Roblox Studio + Luau**
> - Infraestructura local: **Docker + Docker Compose**
> - Observabilidad: **OpenTelemetry**
> - Móvil inicialmente: **Angular responsive + PWA**
> - App móvil futura: **Capacitor**, solo si llega a ser necesario

---

## 1. Qué debes instalar ahora

### 1.1 Visual Studio 2026 Community

**Uso:** backend de Liro, ASP.NET Core, Worker Services, pruebas y depuración en C#.

Descarga oficial:

https://visualstudio.microsoft.com/downloads/

Durante la instalación marca como mínimo:

- **ASP.NET and web development**
- Componentes de **.NET 10**
- Herramientas de contenedores/Docker si aparecen disponibles

Usaremos **Visual Studio Community 2026**.

---

### 1.2 .NET 10 SDK

**Uso:** compilar y ejecutar `Liro.Api`, `Liro.Workers` y los proyectos compartidos.

Descarga oficial:

https://dotnet.microsoft.com/download

Versión base del proyecto:

- **.NET 10 LTS**
- Instalar el **SDK x64**, no solamente el Runtime

A fecha de creación de este documento, .NET 10 es la versión LTS recomendada.

Comprobar después de instalar:

```powershell
dotnet --version
```

---

### 1.3 Visual Studio Code

**Uso:** Angular, TypeScript, extensión del navegador, archivos Docker y configuración.

Descarga oficial:

https://code.visualstudio.com/

Extensiones recomendadas más adelante:

- Angular Language Service
- C#
- Docker
- ESLint
- Prettier
- EditorConfig

No es obligatorio usar VS Code para C#, porque utilizaremos Visual Studio principalmente para el backend.

---

### 1.4 Node.js LTS

**Uso:** Angular CLI, npm, TypeScript y herramientas del frontend.

Descarga oficial:

https://nodejs.org/en/download

Versión recomendada para Liro:

- **Node.js 24 LTS**
- Instalar versión **x64** en Windows normal

Angular 22 soporta Node 24.

Node instala también `npm`.

Comprobar:

```powershell
node --version
npm --version
```

---

### 1.5 Angular CLI

No se descarga como programa independiente. Se instala mediante `npm` después de instalar Node.js.

Documentación oficial:

https://angular.dev/installation

Instalación que utilizaremos:

```powershell
npm install -g @angular/cli@22
```

Comprobar:

```powershell
ng version
```

Versión base:

- **Angular 22**

---

### 1.6 Git

**Uso:** control de versiones de todo Liro.

Descarga oficial para Windows:

https://git-scm.com/download/win

Comprobar:

```powershell
git --version
```

Más adelante configuraremos:

- nombre
- correo
- repositorio remoto
- ramas
- `.gitignore`

GitHub será recomendable para alojar el repositorio:

https://github.com/

---

### 1.7 WSL 2

**Uso:** Docker Desktop utiliza WSL 2 como backend recomendado en Windows.

Documentación oficial:

https://learn.microsoft.com/windows/wsl/install

En PowerShell como administrador:

```powershell
wsl --install
```

Después reinicia Windows si el sistema lo solicita.

Comprobar:

```powershell
wsl --status
```

Si Docker Desktop instala/configura WSL automáticamente y ya funciona correctamente, no es necesario repetir la instalación.

---

### 1.8 Docker Desktop

**Uso:** ejecutar localmente servicios de infraestructura sin instalarlos directamente en Windows.

Con Docker ejecutaremos inicialmente:

- PostgreSQL 18
- Valkey
- servicios auxiliares futuros

Descarga oficial:

https://docs.docker.com/desktop/setup/install/windows-install/

Usar:

- Docker Desktop
- backend **WSL 2**
- Docker Compose

Comprobar:

```powershell
docker --version
docker compose version
```

> **Importante:** no vamos a instalar PostgreSQL ni Valkey directamente en Windows en la configuración recomendada. Los ejecutaremos mediante Docker.

---

### 1.9 Roblox Studio

**Uso:** desarrollar el cliente/juego de Roblox de Liro en Luau.

Instalación oficial:

https://create.roblox.com/docs/studio/setup

Roblox Studio será utilizado para:

- interfaz dentro de Roblox
- HttpService
- comunicación con Liro
- MessagingService
- lógica Luau
- pruebas del juego

---

### 1.10 Google Chrome

**Uso:** desarrollo y pruebas iniciales de la extensión de Liro.

Descarga/instrucciones oficiales:

https://support.google.com/chrome/answer/95346

Documentación oficial de extensiones:

https://developer.chrome.com/docs/extensions/

La extensión se diseñará inicialmente con:

- TypeScript
- Manifest V3
- Content Scripts
- Service Worker
- comunicación con la API de Liro

Más adelante podremos validar también Edge y Firefox.

---

## 2. Herramientas recomendadas, pero no obligatorias

### 2.1 DBeaver Community

**Uso:** ver y administrar PostgreSQL con interfaz gráfica.

Descarga:

https://dbeaver.io/download/

Es opcional, pero lo recomiendo porque facilitará revisar:

- tablas
- índices
- consultas
- datos de items
- histórico de precios
- migraciones

PostgreSQL seguirá ejecutándose dentro de Docker.

---

### 2.2 GitHub Desktop

No es necesario si utilizas Git desde terminal o Visual Studio.

Página oficial:

https://desktop.github.com/

Puede ser útil si prefieres una interfaz gráfica para Git.

---

### 2.3 Cliente para probar APIs

Al principio podemos trabajar con Swagger/OpenAPI que generará ASP.NET Core.

Por eso **Postman no es obligatorio**.

Si posteriormente queremos un cliente dedicado para APIs podremos incorporar uno, pero no es necesario para arrancar Liro.

---

## 3. Servicios que NO debes instalar directamente

### PostgreSQL

Sitio oficial:

https://www.postgresql.org/download/windows/

Versión base:

- **PostgreSQL 18**

Sin embargo, para Liro lo ejecutaremos en Docker.

Por tanto:

**NO instalar PostgreSQL manualmente en Windows por ahora.**

---

### Valkey

Sitio oficial:

https://valkey.io/download/

Versión base prevista:

- **Valkey 9.x**

También se ejecutará mediante Docker.

Por tanto:

**NO instalar Valkey manualmente en Windows.**

Valkey se utilizará para:

- precios actuales
- RAP actual
- rankings
- trending
- estado de mercado
- cache
- datos temporales
- soporte para realtime

---

## 4. Herramientas que se añadirán dentro del proyecto

Estas no requieren un instalador global ahora.

### SignalR

Forma parte del ecosistema ASP.NET Core.

Uso:

- precios en tiempo real en Angular
- cambios de mercado
- rankings
- watchlists
- notificaciones activas

---

### Entity Framework Core

Se añadirá al backend mediante paquetes NuGet.

Uso:

- acceso a PostgreSQL
- migraciones
- modelos persistentes

La herramienta CLI podrá instalarse posteriormente con:

```powershell
dotnet tool install --global dotnet-ef
```

---

### OpenTelemetry

Se añadirá mediante paquetes NuGet/configuración.

Uso:

- trazas
- métricas
- seguimiento de llamadas
- diagnóstico de Workers
- diagnóstico de consultas a Roblox
- seguimiento del pipeline de precios

No requiere instalar un programa ahora.

---

### Capacitor

**NO instalar ahora.**

Solo lo incorporaremos si en el futuro decidimos publicar una app Android/iOS basada en Angular.

Sitio oficial:

https://capacitorjs.com/

Primero construiremos:

- web responsive
- experiencia móvil
- PWA

---

## 5. Cloudflare

No necesitas instalar nada de Cloudflare todavía.

Cuando lleguemos al despliegue podremos utilizar Cloudflare para cosas como:

- DNS
- CDN
- protección
- frontend estático
- edge

Wrangler solo será necesario si usamos Workers/Pages u otros servicios específicos.

Documentación:

https://developers.cloudflare.com/

---

## 6. Resumen: qué instalar en tu PC

Instalar en este orden:

1. **Git**
   - https://git-scm.com/download/win

2. **Visual Studio 2026 Community**
   - https://visualstudio.microsoft.com/downloads/

3. **.NET 10 SDK**
   - https://dotnet.microsoft.com/download

4. **Visual Studio Code**
   - https://code.visualstudio.com/

5. **Node.js 24 LTS**
   - https://nodejs.org/en/download

6. **Angular CLI 22**
   - https://angular.dev/installation
   - instalar con `npm install -g @angular/cli@22`

7. **WSL 2**
   - https://learn.microsoft.com/windows/wsl/install

8. **Docker Desktop**
   - https://docs.docker.com/desktop/setup/install/windows-install/

9. **Roblox Studio**
   - https://create.roblox.com/docs/studio/setup

10. **Google Chrome**
    - https://support.google.com/chrome/answer/95346

11. **DBeaver Community — recomendado**
    - https://dbeaver.io/download/

---

## 7. Lo que correrá en Docker

No instalar manualmente:

```text
PostgreSQL 18
Valkey 9.x
```

Más adelante tendremos un archivo:

```text
docker-compose.yml
```

que levantará el entorno local de Liro.

Arquitectura local aproximada:

```text
Windows
│
├── Visual Studio
│   ├── Liro.Api
│   └── Liro.Workers
│
├── VS Code
│   ├── Liro.Web
│   └── Liro.Extension
│
├── Roblox Studio
│   └── Liro Roblox
│
└── Docker Desktop
    ├── PostgreSQL
    └── Valkey
```

---

## 8. Verificación final del entorno

Cuando termines de instalar todo, abre PowerShell y verifica:

```powershell
dotnet --version
node --version
npm --version
ng version
git --version
docker --version
docker compose version
wsl --status
```

Todo debería responder sin errores.

---

## 9. Versiones base de Liro

| Tecnología | Versión objetivo |
|---|---|
| .NET | 10 LTS |
| ASP.NET Core | 10 |
| C# | 14 |
| Angular | 22 |
| Node.js | 24 LTS |
| PostgreSQL | 18 |
| Valkey | 9.x |
| Docker | versión estable actual |
| Git | versión estable actual |
| Visual Studio | 2026 Community |
| Roblox Studio | versión estable actual |
| Browser Extension | Manifest V3 |

Estas versiones deberán quedar fijadas/documentadas en el repositorio para evitar diferencias entre entornos.

---

## 10. Lo que todavía NO necesitamos

No instalar por ahora:

- Kubernetes
- RabbitMQ
- Kafka
- Elasticsearch
- TimescaleDB
- Redis Server
- PostgreSQL Server local
- Android Studio
- Xcode
- Flutter
- React Native
- Ionic
- Capacitor
- herramientas de microservicios
- herramientas de Kubernetes

La filosofía del proyecto base es:

> empezar con una arquitectura profesional pero sin infraestructura innecesaria.

---

## 11. Arquitectura de desarrollo inicial

```text
                         ROBLOX
                            │
                            ▼
                     Liro.Workers
                         .NET 10
                            │
                            ▼
                    ┌──────────────┐
                    │  Liro Core   │
                    └──────┬───────┘
                           │
                 ┌─────────┴─────────┐
                 ▼                   ▼
            PostgreSQL             Valkey
              Docker               Docker
                 │                   │
                 └─────────┬─────────┘
                           ▼
                       Liro.Api
                       .NET 10
                           │
             ┌─────────────┼─────────────┐
             ▼             ▼             ▼
        Angular Web     Extension      Roblox
        Angular 22     TypeScript       Luau
```

---

## 12. Próximo paso después de instalar

No crearemos todavía funcionalidades de trading.

El siguiente paso será generar el **proyecto base de Liro** con:

```text
Liro/
├── backend/
├── web/
├── extension/
├── roblox/
├── infrastructure/
├── docs/
└── tests/
```

Y dentro del backend:

```text
Liro.Api
Liro.Workers
Liro.Domain
Liro.Application
Liro.Infrastructure
```

con los módulos iniciales:

```text
Identity
Catalog
RobloxIntegration
MarketData
Valuation
Trading
Portfolio
Notifications
```

Después prepararemos:

- solución `.sln`
- Docker Compose
- PostgreSQL
- Valkey
- primera migración
- configuración de entornos
- estructura Angular
- estructura de Workers
- integración inicial con Roblox

---

**Documento:** Liro Development Environment  
**Sistema objetivo inicial:** Windows 11 x64  
**Proyecto:** Liro  
**Estado:** Base inicial
