# LIRO — Manual para arrancar el proyecto como desarrollador

## Objetivo

Este documento explica qué necesita hacer un desarrollador que recibe el repositorio de **Liro** y quiere levantarlo localmente.

Este manual asume que la PC ya tiene instalado lo indicado en:

**LIRO_MANUAL_INSTALACION.md**

---

## 1. Requisitos obligatorios

El desarrollador debe tener:

- Git
- Visual Studio con soporte ASP.NET Core
- .NET 10 SDK
- Node.js 24 LTS
- Angular CLI 22
- Docker Desktop
- WSL 2 en Windows
- Visual Studio Code recomendado
- Roblox Studio si trabajará en el cliente Roblox
- Chrome si trabajará en la extensión

No necesita instalar manualmente:

- PostgreSQL
- Valkey

Esos servicios se levantarán mediante Aspire + Docker.

---

## 2. Obtener el repositorio

Clonar el repositorio de Liro:

```powershell
git clone <URL-DEL-REPOSITORIO-LIRO>
cd Liro
```

La URL real se añadirá cuando el repositorio oficial sea creado.

---

## 3. Estructura esperada del repositorio

```text
Liro/
│
├── backend/
│   ├── Liro.sln
│   └── src/
│       ├── Liro.AppHost/
│       ├── Liro.Api/
│       ├── Liro.Workers/
│       ├── Liro.Domain/
│       ├── Liro.Application/
│       └── Liro.Infrastructure/
│
├── web/
│   └── liro-web/
│
├── extension/
│   └── liro-extension/
│
├── roblox/
│   └── ...
│
├── docs/
│
└── README.md
```

La estructura definitiva se generará cuando creemos el proyecto base.

---

## 4. Arranque de Docker Desktop

Antes de iniciar Liro:

1. Abrir Docker Desktop.
2. Esperar hasta que indique que el engine está iniciado.

Verificar:

```powershell
docker info
```

Si Docker no está disponible, PostgreSQL y Valkey no podrán arrancar localmente.

---

## 5. Backend .NET

Abrir:

```text
backend/Liro.sln
```

en Visual Studio.

Restaurar dependencias si Visual Studio no lo hace automáticamente:

```powershell
dotnet restore
```

---

## 6. Configuración local y secretos

Los secretos NO se guardarán en Git.

El repositorio deberá incluir archivos de ejemplo, por ejemplo:

```text
.env.example
appsettings.Development.example.json
```

El desarrollador copiará los ejemplos y añadirá sus valores locales.

Ejemplos de información que podrá necesitar:

- configuración de Roblox/Open Cloud
- claves de APIs externas autorizadas
- URLs locales
- secretos de autenticación
- configuración de desarrollo

Nunca subir al repositorio:

```text
API keys reales
contraseñas
tokens
cookies
secretos Roblox
connection strings con credenciales reales
```

Para .NET se utilizará preferentemente:

- User Secrets para desarrollo
- variables de entorno para infraestructura

---

## 7. Frontend Angular

Entrar en:

```powershell
cd web/liro-web
```

Instalar dependencias exactamente desde el lockfile:

```powershell
npm ci
```

`npm ci` será preferido sobre `npm install` para onboarding y CI porque instala las versiones fijadas por el proyecto.

El AppHost podrá integrar el frontend posteriormente; mientras se termina esa integración, Angular también podrá ejecutarse directamente con:

```powershell
ng serve
```

---

## 8. Arranque normal del proyecto

La experiencia objetivo de Liro será:

1. Docker Desktop iniciado.
2. Abrir `Liro.sln`.
3. Seleccionar `Liro.AppHost` como proyecto de inicio.
4. Presionar `F5`.

Aspire levantará/orquestará:

```text
Liro.Api
Liro.Workers
PostgreSQL
Valkey
```

y mostrará un dashboard local con:

- recursos
- endpoints
- logs
- traces
- estado de servicios

El desarrollador podrá depurar `Liro.Api` y `Liro.Workers` desde Visual Studio.

---

## 9. Qué hace Docker y qué hace Visual Studio

No todo Liro corre obligatoriamente dentro de Docker.

### Directamente desde .NET / Visual Studio

```text
Liro.Api
Liro.Workers
Liro.AppHost
```

Esto permite:

- breakpoints
- hot reload
- debugging
- IntelliSense
- ejecución rápida

### Dentro de Docker

```text
PostgreSQL
Valkey
```

Docker aporta:

- mismas versiones para todos
- mismas configuraciones
- entorno reproducible
- cero instalación manual de servidores de base de datos/cache

---

## 10. Base de datos

El desarrollador NO crea manualmente una base PostgreSQL local.

Aspire/Docker levantará PostgreSQL.

Las tablas se gestionarán mediante migraciones de Entity Framework Core.

El flujo será:

```text
Docker inicia PostgreSQL
        ↓
Liro aplica/verifica migraciones
        ↓
Base lista
        ↓
Liro.Api / Liro.Workers
```

El proceso exacto de migraciones quedará automatizado/documentado en el proyecto base.

---

## 11. Valkey

Valkey también será levantado automáticamente.

Se utilizará para:

- cache
- precios actuales
- RAP actual
- rankings
- trending
- estado temporal de mercado
- información de alta frecuencia

El desarrollador no necesita configurar Valkey manualmente.

---

## 12. Extensión del navegador

Si se trabajará en la extensión:

```powershell
cd extension/liro-extension
npm ci
```

La extensión se cargará en Chrome mediante:

```text
chrome://extensions
→ Developer mode
→ Load unpacked
```

El directorio exacto de salida se documentará cuando generemos el proyecto de extensión.

---

## 13. Roblox

Si se trabajará en el cliente Roblox:

1. Abrir Roblox Studio.
2. Abrir el proyecto/experiencia de Liro.
3. Configurar las variables de desarrollo requeridas.
4. Tener activo el backend local o entorno de integración correspondiente.

La integración Roblox se conectará únicamente a las APIs públicas definidas por Liro; la lógica de precios y análisis permanecerá en Liro Core.

---

## 14. Comprobación rápida antes de reportar un error

Antes de concluir que Liro no arranca, verificar:

```powershell
dotnet --version
node --version
npm --version
docker info
```

Luego comprobar en Aspire:

```text
Liro.Api       Running
Liro.Workers   Running
PostgreSQL     Running
Valkey         Running
```

Si falla un recurso, revisar sus logs desde el dashboard de Aspire.

---

## 15. Flujo diario recomendado

Al comenzar:

```text
1. git pull
2. iniciar Docker Desktop
3. abrir Liro.sln
4. F5 en Liro.AppHost
5. trabajar
```

Si se modificó `package-lock.json`:

```powershell
npm ci
```

Si se modificaron dependencias .NET:

```powershell
dotnet restore
```

---

## 16. Al terminar

Antes de subir cambios:

```powershell
git status
```

Verificar:

- no hay secretos
- no hay archivos locales innecesarios
- backend compila
- frontend compila
- pruebas relevantes pasan

El flujo de ramas, Pull Requests y CI/CD se documentará por separado.

---

## 17. Qué debe poder hacer un desarrollador nuevo

El objetivo del proyecto base es que, después de preparar su PC una sola vez, un desarrollador nuevo pueda hacer:

```text
git clone
   ↓
configurar secretos locales
   ↓
npm ci
   ↓
abrir Liro.sln
   ↓
F5 en Liro.AppHost
   ↓
Liro funcionando
```

Sin instalar manualmente:

```text
PostgreSQL
Valkey
RabbitMQ
Kafka
otros servicios
```

---

## 18. Regla del entorno local

**Visual Studio/Aspire orquesta. Docker ejecuta la infraestructura.**

```text
                 Visual Studio
                       │
                       ▼
                  Liro.AppHost
                    Aspire
                       │
         ┌─────────────┼─────────────┐
         │             │             │
         ▼             ▼             ▼
     Liro.Api     Liro.Workers   Infraestructura
       .NET           .NET            │
                               ┌──────┴──────┐
                               ▼             ▼
                          PostgreSQL       Valkey
                            Docker          Docker
```

Esta será la experiencia oficial de desarrollo local de Liro.
