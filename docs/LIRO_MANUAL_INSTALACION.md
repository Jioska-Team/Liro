# LIRO — Manual de instalación del entorno de desarrollo

## Objetivo

Este documento prepara una PC Windows desde cero para desarrollar **Liro**.

La arquitectura base utiliza:

- .NET 10 LTS / ASP.NET Core
- Angular 22
- Node.js 24 LTS
- .NET Aspire para orquestación local
- Docker Desktop para PostgreSQL y Valkey
- PostgreSQL 18
- Valkey
- Visual Studio
- Visual Studio Code
- Git
- Roblox Studio
- Chrome para la extensión Manifest V3

> En desarrollo, **Liro.Api** y **Liro.Workers** se depuran desde Visual Studio.  
> PostgreSQL y Valkey se ejecutan como contenedores.  
> **Liro.AppHost (Aspire)** será el punto de arranque del entorno completo.

---

## 1. Requisitos del equipo

Recomendado:

- Windows 11 x64
- 16 GB RAM o más
- Virtualización habilitada en BIOS/UEFI
- Al menos 30 GB libres para SDKs, imágenes y contenedores
- Acceso a Internet

Docker Desktop con WSL 2 requiere virtualización y una versión compatible de WSL.

---

## 2. Instalar Git

Descarga oficial:

https://git-scm.com/download/win

Instalar con las opciones predeterminadas.

Verificar:

```powershell
git --version
```

---

## 3. Instalar Visual Studio

Descarga oficial:

https://visualstudio.microsoft.com/downloads/

Instalar **Visual Studio Community**.

Seleccionar como mínimo:

- ASP.NET and web development
- .NET 10 SDK / herramientas .NET
- Git tools
- Container development tools si aparecen disponibles

Visual Studio será el IDE principal para:

- Liro.Api
- Liro.Workers
- Liro.AppHost
- librerías de dominio/aplicación/infraestructura
- pruebas .NET

---

## 4. Instalar .NET 10 SDK

Descarga oficial:

https://dotnet.microsoft.com/download

Instalar:

- .NET 10 SDK x64

No basta con instalar solamente el Runtime.

Verificar:

```powershell
dotnet --version
```

Debe devolver una versión `10.x`.

---

## 5. Instalar Visual Studio Code

Descarga oficial:

https://code.visualstudio.com/

Uso principal:

- Angular
- TypeScript
- extensión del navegador
- archivos de configuración
- documentación Markdown

Extensiones recomendadas:

- Angular Language Service
- ESLint
- Prettier
- Docker
- EditorConfig
- C#

---

## 6. Instalar Node.js 24 LTS

Descarga oficial:

https://nodejs.org/en/download

Instalar una versión **Node.js 24 LTS compatible con Angular 22**.

Angular 22 requiere Node.js 24.15.0 o superior dentro de la rama 24.

Verificar:

```powershell
node --version
npm --version
```

---

## 7. Instalar Angular CLI 22

Documentación oficial:

https://angular.dev/installation

Instalar:

```powershell
npm install -g @angular/cli@22
```

Verificar:

```powershell
ng version
```

---

## 8. Instalar/actualizar WSL 2

Documentación oficial:

https://learn.microsoft.com/windows/wsl/install

En PowerShell como administrador:

```powershell
wsl --install
```

Luego:

```powershell
wsl --update
wsl --status
```

Docker Desktop recomienda WSL 2 y requiere una versión compatible.

Reiniciar Windows si el instalador lo solicita.

---

## 9. Instalar Docker Desktop

Descarga oficial:

https://docs.docker.com/desktop/setup/install/windows-install/

Configuración recomendada:

- Backend WSL 2
- Linux containers
- Docker Compose habilitado

Docker se utilizará para ejecutar:

- PostgreSQL 18
- Valkey
- otros servicios de infraestructura futuros

Verificar:

```powershell
docker --version
docker compose version
```

> Cada desarrollador que ejecute la infraestructura local mediante contenedores necesita un runtime compatible.  
> Para Liro, el runtime oficial recomendado será Docker Desktop.

---

## 10. Aspire

Documentación oficial:

https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling

Para un AppHost en C#, Aspire requiere .NET 10.

Aspire será utilizado para que el desarrollador pueda arrancar el entorno completo desde un único punto:

```text
Liro.AppHost
    ├── Liro.Api
    ├── Liro.Workers
    ├── PostgreSQL
    └── Valkey
```

El objetivo es que el flujo habitual sea:

```text
Abrir Liro.sln
→ seleccionar Liro.AppHost
→ F5
→ entorno completo levantado
```

Docker seguirá siendo el motor que ejecuta PostgreSQL y Valkey, pero el desarrollador no tendrá que levantar manualmente cada contenedor.

---

## 11. Instalar Roblox Studio

Documentación oficial:

https://create.roblox.com/docs/studio/setup

Roblox Studio se utilizará para:

- cliente/juego Liro
- Luau
- HttpService
- MessagingService
- pruebas de integración con Liro

---

## 12. Instalar Google Chrome

Descarga oficial:

https://www.google.com/chrome/

Documentación de extensiones:

https://developer.chrome.com/docs/extensions/

La extensión Liro se desarrollará inicialmente para:

- Chrome/Chromium
- Manifest V3
- TypeScript

Después se validará compatibilidad con Edge y Firefox.

---

## 13. Instalar DBeaver Community — recomendado

Descarga oficial:

https://dbeaver.io/download/

No es obligatorio.

Se utilizará solamente como cliente visual para conectarse al PostgreSQL que estará corriendo en Docker.

---

## 14. Qué NO instalar manualmente

No instalar directamente en Windows:

- PostgreSQL Server
- Valkey
- RabbitMQ
- Kafka
- Kubernetes
- Elasticsearch
- TimescaleDB

PostgreSQL y Valkey serán administrados por el entorno local de Liro mediante Aspire + Docker.

---

## 15. Verificación final

Ejecutar:

```powershell
git --version
dotnet --version
node --version
npm --version
ng version
docker --version
docker compose version
wsl --status
```

También verificar:

- Visual Studio abre correctamente.
- Docker Desktop está iniciado.
- Roblox Studio abre correctamente.
- Chrome está instalado.

---

## 16. Stack oficial del proyecto base

| Componente | Tecnología |
|---|---|
| Backend | ASP.NET Core / .NET 10 |
| Lenguaje backend | C# |
| Web | Angular 22 |
| Runtime frontend | Node.js 24 LTS |
| Orquestación local | .NET Aspire |
| Contenedores | Docker Desktop |
| Base de datos | PostgreSQL 18 |
| Cache / datos calientes | Valkey |
| Realtime web | SignalR |
| ORM | Entity Framework Core |
| Observabilidad | OpenTelemetry |
| Extensión | TypeScript + Manifest V3 |
| Roblox | Roblox Studio + Luau |
| Control de versiones | Git |

---

## 17. Resultado esperado

Una vez instalado el entorno, la PC debe estar preparada para trabajar con:

```text
Visual Studio
   │
   ▼
Liro.AppHost
   │
   ├── Liro.Api
   ├── Liro.Workers
   ├── PostgreSQL (Docker)
   └── Valkey (Docker)

VS Code
   ├── Liro.Web
   └── Liro.Extension

Roblox Studio
   └── Liro Roblox
```

El siguiente documento que debe seguir un desarrollador es:

**LIRO_MANUAL_DESARROLLADOR.md**
