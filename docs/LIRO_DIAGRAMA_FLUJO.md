# LIRO — Diagrama de flujo general del sistema

## Objetivo

Este documento describe el flujo principal de **Liro**, desde que Roblox expone o modifica información de un item hasta que ese cambio aparece en:

- la web de Liro;
- la extensión del navegador;
- el juego de Roblox.

Liro tendrá una única fuente interna de verdad.  
La web, la extensión y el juego **no calcularán precios ni análisis por separado**.

---

# 1. Flujo general de Liro

```mermaid
flowchart TD

    A[Roblox Marketplace / Open Cloud / APIs de Collectibles]

    A --> B[Discovery Worker]
    A --> C[Market Worker]
    A --> D[Inventory Worker]

    B --> E[Roblox Integration / Adapter]
    C --> E
    D --> E

    E --> F[Normalizer]

    F --> G[Catalog]
    F --> H[Market Data]
    F --> I[Inventory Data]

    G --> J[(PostgreSQL)]
    H --> J
    I --> J

    H --> K[Valuation Engine]
    K --> L[Liro Value]
    K --> M[Projected Detection]
    K --> N[Demand / Liquidity / Trend]
    K --> O[Volatility / Confidence]

    L --> P[(Valkey)]
    M --> P
    N --> P
    O --> P
    H --> P

    P --> Q[Liro API / Realtime Gateway]

    Q --> R[Angular Web]
    Q --> S[Browser Extension]
    Q --> T[Roblox Game]

    R --> U[SignalR]
    S --> V[REST + Realtime cuando esté activa]
    T --> W[Messaging + Snapshot HTTP]
```

---

# 2. Principio principal

```text
Roblox
   ↓
Liro obtiene los datos
   ↓
Liro normaliza
   ↓
Liro guarda los datos originales
   ↓
Liro calcula métricas propias
   ↓
Liro publica el estado actual
   ↓
Web / Extensión / Roblox reciben la misma información
```

La regla será:

> **Roblox entrega datos. Liro transforma esos datos en información de mercado.**

Ejemplo:

```text
Roblox
────────────────────
RAP
Lowest Resale
Volumen
Resellers
Supply
Histórico

        ↓

Liro
────────────────────
Liro Value
Projected Score
Demand Score
Liquidity Score
Trend
Volatility
Trade Analysis
```

---

# 3. Flujo de descubrimiento de items

El `Discovery Worker` se encargará de descubrir:

- nuevos Limiteds;
- nuevos collectibles;
- items existentes que cambiaron;
- items normales que se convierten en Limited;
- cambios importantes de metadata.

```mermaid
flowchart TD

    A[Discovery Worker consulta Roblox]

    A --> B{¿Item existe en Liro?}

    B -- No --> C[Crear Item]
    C --> D[Guardar AssetId]
    D --> E[Resolver CollectibleItemId]
    E --> F[Guardar metadata]
    F --> G{¿Es Limited?}

    B -- Sí --> H[Comparar estado actual con estado guardado]
    H --> I{¿Cambió?}

    I -- No --> J[Finalizar revisión]
    I -- Sí --> K[Registrar cambio de ciclo de vida]
    K --> G

    G -- No --> L[Guardar como Item normal]
    G -- Sí --> M[Inicializar Market Data]

    M --> N[Consultar resale-data]
    M --> O[Consultar resellers]
    M --> P[Importar histórico disponible]

    N --> Q[(PostgreSQL)]
    O --> Q
    P --> Q

    Q --> R[Activar monitoreo de mercado]
```

---

# 4. Item normal que se convierte en Limited

Liro no creará un item nuevo.

Mantendrá la misma identidad interna y registrará el cambio:

```text
ANTES

Liro Item Id:        8392
Roblox Asset Id:     123456
CollectibleItemId:   null
Estado:              Normal


DESPUÉS

Liro Item Id:        8392
Roblox Asset Id:     123456
CollectibleItemId:   abc-def-123
Estado:              Limited
```

El flujo será:

```mermaid
flowchart LR

    A[Item normal conocido]
    --> B[Discovery detecta cambio]
    --> C[Normal → Limited]
    --> D[Resolver CollectibleItemId]
    --> E[Importar datos de mercado]
    --> F[Guardar histórico]
    --> G[Valuation Engine]
    --> H[Publicar en Liro]
```

También se guardará un historial de ciclo de vida:

```text
ItemLifecycle

ItemId   Fecha         Evento
────────────────────────────────
8392     2025-04-12    Discovered
8392     2025-04-12    Normal
8392     2026-10-03    BecameLimited
```

---

# 5. Flujo de actualización de precios

El `Market Worker` se encargará de Limiteds que ya conocemos.

```mermaid
flowchart TD

    A[Market Worker]

    A --> B[Seleccionar items a revisar]
    B --> C[Consultar Roblox]

    C --> D[RAP]
    C --> E[Lowest Resale]
    C --> F[Volume]
    C --> G[Resellers]
    C --> H[Histórico disponible]

    D --> I[Normalizer]
    E --> I
    F --> I
    G --> I
    H --> I

    I --> J{¿Cambió el mercado?}

    J -- No --> K[Actualizar timestamp]
    J -- Sí --> L[Guardar nuevo Market Snapshot]

    L --> M[(PostgreSQL)]
    L --> N[Valuation Engine]

    N --> O[Liro Value]
    N --> P[Projected Score]
    N --> Q[Demand]
    N --> R[Liquidity]
    N --> S[Trend]

    O --> T[(Valkey)]
    P --> T
    Q --> T
    R --> T
    S --> T

    T --> U[MarketChanged Event]

    U --> V[Angular / SignalR]
    U --> W[Extension / API]
    U --> X[Roblox / Messaging]
```

---

# 6. Frecuencia inteligente de actualización

No todos los Limiteds se consultarán con la misma frecuencia.

```mermaid
flowchart TD

    A[Todos los Limiteds]

    A --> B{Clasificar actividad}

    B --> C[HOT]
    B --> D[NORMAL]
    B --> E[COLD]

    C --> F[Trending / alto volumen / cambios frecuentes]
    D --> G[Actividad normal]
    E --> H[Pocas ventas / poca actividad]

    F --> I[Consultar con mayor frecuencia]
    G --> J[Consultar frecuencia media]
    H --> K[Consultar con menor frecuencia]
```

La frecuencia podrá aumentar temporalmente cuando:

```text
sube el volumen
cambian los resellers
cambia rápidamente el precio
el item se vuelve trending
muchos usuarios lo están consultando
```

Esto evita hacer:

```text
50.000 items × consulta cada 5 segundos
```

---

# 7. Flujo de almacenamiento

Liro separará los datos en tres niveles.

```mermaid
flowchart LR

    A[Roblox Raw Data]
    --> B[Liro Normalized Data]
    --> C[Liro Intelligence]
```

## 7.1 Datos originales / normalizados

Ejemplos:

```text
Item
AssetId
CollectibleItemId
RAP
LowestResale
Volume
Sales
Resellers
Supply
Historical Price
```

Se almacenan principalmente en:

```text
PostgreSQL
```

---

## 7.2 Histórico

Nunca sobrescribiremos el histórico.

Ejemplo:

```text
MarketHistory

Item        Fecha       RAP       Volume
────────────────────────────────────────
Item A      10:00       4,100       120
Item A      10:05       4,110       140
Item A      10:10       4,095       128
```

PostgreSQL será la fuente persistente de esta información.

---

## 7.3 Estado actual

El estado actual se mantendrá también en:

```text
Valkey
```

Ejemplo conceptual:

```text
Item A

RAP:             4,095
Lowest Resale:   4,180
Liro Value:      4,120
Projected:       false
Demand:          0.78
Liquidity:       0.64
Trend:           +2.4%
```

Valkey permitirá que la web y otros clientes obtengan el estado actual rápidamente.

---

# 8. Flujo del Valuation Engine

Roblox no calculará las métricas propias de Liro.

```mermaid
flowchart TD

    A[Market Data]

    A --> B[RAP]
    A --> C[Lowest Resale]
    A --> D[Volume]
    A --> E[Sales]
    A --> F[Resellers]
    A --> G[Historical Data]

    B --> H[Valuation Engine]
    C --> H
    D --> H
    E --> H
    F --> H
    G --> H

    H --> I[Liro Value]
    H --> J[Projected Score]
    H --> K[Demand Score]
    H --> L[Liquidity Score]
    H --> M[Trend]
    H --> N[Volatility]
    H --> O[Confidence]
```

Cada cálculo deberá registrar:

```text
AlgorithmVersion
```

para poder saber con qué versión del algoritmo se obtuvo cada valor.

---

# 9. Flujo de detección de Projected

Ejemplo conceptual:

```text
Histórico normal:
52K → 53K → 54K

RAP actual:
100K

Lowest Resale:
55K

Volumen:
inusual
```

El motor analiza:

```mermaid
flowchart TD

    A[RAP]
    B[Lowest Resale]
    C[Histórico]
    D[Volumen]
    E[Resellers]

    A --> F[Projected Analyzer]
    B --> F
    C --> F
    D --> F
    E --> F

    F --> G[Projected Score 0.91]

    G --> H{¿Supera umbral?}

    H -- Sí --> I[Projected = High Risk]
    H -- No --> J[Projected = Normal]
```

Liro preferirá almacenar una puntuación:

```text
ProjectedScore = 0.91
```

en lugar de solamente:

```text
Projected = true
```

---

# 10. Flujo de análisis de trade

La extensión y la web usarán el mismo `Trade Analysis Engine`.

```mermaid
flowchart TD

    A[Usuario abre un Trade]

    A --> B[Extensión detecta items ofrecidos]
    B --> C[Solicitar análisis a Liro]

    C --> D[Trade Analysis Engine]

    D --> E[Consultar RAP]
    D --> F[Consultar Liro Value]
    D --> G[Projected]
    D --> H[Demand]
    D --> I[Liquidity]
    D --> J[Trend]

    E --> K[Resultado del Trade]
    F --> K
    G --> K
    H --> K
    I --> K
    J --> K

    K --> L[Extensión muestra información]
```

La extensión no tendrá su propio algoritmo.

Ejemplo de resultado:

```text
YOU GIVE
─────────────
Value: 450K

YOU RECEIVE
─────────────
Value: 482K

Difference: +32K

Projected Risk: Low
Demand: High
Liquidity: Medium
Trend: +3.8%
```

---

# 11. Flujo de tiempo real para la web

La web Angular utilizará SignalR.

```mermaid
sequenceDiagram

    participant Roblox
    participant Worker as Liro.Workers
    participant Core as Liro Core
    participant Cache as Valkey
    participant API as Liro.Api
    participant Web as Angular

    Worker->>Roblox: Consultar Market Data
    Roblox-->>Worker: Nuevos datos
    Worker->>Core: Normalizar / procesar
    Core->>Cache: Actualizar estado actual
    Core->>API: MarketChanged
    API-->>Web: SignalR PriceUpdated
    Web->>Web: Actualizar interfaz
```

Ejemplo:

```text
4,107
  ↓
4,129
```

sin recargar la página.

---

# 12. Flujo de la extensión

Manifest V3 utiliza service workers, por lo que no dependeremos de una conexión permanente.

```mermaid
sequenceDiagram

    participant User as Usuario
    participant Extension as Liro Extension
    participant API as Liro API
    participant Core as Trade Analyzer

    User->>Extension: Abre página de trade/item
    Extension->>Extension: Detectar AssetIds
    Extension->>API: Solicitar información actual
    API->>Core: Analizar items / trade
    Core-->>API: Resultado
    API-->>Extension: Market + Trade Analysis
    Extension-->>User: Mostrar información
```

Cuando la extensión esté activa podrá recibir información realtime si aporta valor, pero siempre podrá reconstruir su estado consultando la API.

---

# 13. Flujo de tiempo real dentro de Roblox

Roblox tendrá un modelo híbrido:

```text
PUSH
+
SNAPSHOT
```

```mermaid
sequenceDiagram

    participant Core as Liro Core
    participant Cloud as Roblox Open Cloud
    participant Game as Roblox Server
    participant API as Liro API

    Core->>Cloud: Publicar MarketChanged
    Cloud-->>Game: MessagingService

    Note over Game: Actualización rápida

    Game->>API: Solicitar snapshot periódico
    API-->>Game: Estado actual del mercado

    Note over Game: Corrige mensajes perdidos
```

Así evitamos depender exclusivamente de `MessagingService`, cuya entrega no debe ser nuestra única fuente de sincronización.

---

# 14. Flujo de inventarios

Los inventarios no se actualizarán permanentemente para todos los usuarios.

```mermaid
flowchart TD

    A[Usuario abre Portfolio]

    A --> B{¿Inventario cacheado y reciente?}

    B -- Sí --> C[Usar inventario almacenado]

    B -- No --> D[Consultar Roblox Inventory API]
    D --> E[Normalizar collectibles]
    E --> F[(PostgreSQL)]
    F --> G[Actualizar cache]
    G --> C

    C --> H[Obtener valores actuales]
    H --> I[Calcular Portfolio]
    I --> J[Mostrar al usuario]
```

---

# 15. Flujo de datos completo

Este es el flujo central de la plataforma.

```mermaid
flowchart TB

    subgraph Roblox["ROBLOX"]
        R1[Catalog / Marketplace]
        R2[Collectibles]
        R3[Resale Data]
        R4[Resellers]
        R5[Inventory]
    end

    subgraph Ingestion["LIRO INGESTION"]
        D1[Discovery Worker]
        D2[Market Worker]
        D3[Inventory Worker]
        RA[Roblox Adapter]
        N[Normalizer]
    end

    subgraph Core["LIRO CORE"]
        C1[Catalog]
        C2[Market Data]
        C3[Valuation]
        C4[Trading]
        C5[Portfolio]
    end

    subgraph Storage["DATA"]
        PG[(PostgreSQL)]
        VK[(Valkey)]
    end

    subgraph Platform["LIRO PLATFORM"]
        API[Liro.Api]
        RT[Realtime Gateway]
    end

    subgraph Clients["CLIENTES"]
        W[Angular Web]
        E[Browser Extension]
        G[Roblox Game]
    end

    R1 --> D1
    R2 --> D1
    R3 --> D2
    R4 --> D2
    R5 --> D3

    D1 --> RA
    D2 --> RA
    D3 --> RA

    RA --> N

    N --> C1
    N --> C2
    N --> C5

    C1 --> PG
    C2 --> PG
    C5 --> PG

    C2 --> C3
    C3 --> C4

    C2 --> VK
    C3 --> VK
    C4 --> VK

    PG --> API
    VK --> API

    API --> RT

    RT --> W
    API --> E
    RT --> E
    API --> G
    RT --> G
```

---

# 16. Flujo de desarrollo local

El entorno de desarrollo se arrancará desde Visual Studio mediante Aspire.

```mermaid
flowchart TD

    A[Desarrollador]

    A --> B[Iniciar Docker Desktop]
    B --> C[Abrir Liro.sln]
    C --> D[Seleccionar Liro.AppHost]
    D --> E[Presionar F5]

    E --> F[.NET Aspire]

    F --> G[Liro.Api]
    F --> H[Liro.Workers]
    F --> I[PostgreSQL Container]
    F --> J[Valkey Container]

    G --> K[Aspire Dashboard]
    H --> K
    I --> K
    J --> K
```

La experiencia objetivo será:

```text
Docker Desktop iniciado
        ↓
abrir Liro.sln
        ↓
F5
        ↓
Liro completo levantado
```

---

# 17. Orden recomendado de implementación

```mermaid
flowchart TD

    A[1. Proyecto Base]
    --> B[2. PostgreSQL + Valkey + Aspire]
    --> C[3. Roblox Integration]
    --> D[4. Discovery Worker]
    --> E[5. Market Worker]
    --> F[6. Market Data]
    --> G[7. Valuation Engine]
    --> H[8. Liro API]
    --> I[9. SignalR]
    --> J[10. Angular Web]
    --> K[11. Trade Analysis]
    --> L[12. Browser Extension]
    --> M[13. Roblox Game Integration]
    --> N[14. PWA / Mobile]
```

## Fase 1 — Base

```text
Liro.sln
Aspire
Docker
PostgreSQL
Valkey
estructura modular
observabilidad
```

## Fase 2 — Datos Roblox

```text
Roblox Adapter
Catalog
Discovery
Collectibles
Market Data
```

## Fase 3 — Mercado

```text
RAP
Lowest Resale
Volume
History
Resellers
Market snapshots
```

## Fase 4 — Inteligencia Liro

```text
Liro Value
Projected
Demand
Liquidity
Trend
Volatility
```

## Fase 5 — Plataforma

```text
API
SignalR
Authentication
Realtime
```

## Fase 6 — Web

```text
Market
Item pages
Charts
Profiles
Portfolio
Trade Ads
Watchlist
```

## Fase 7 — Extensión

```text
Item information
Trade analysis
Projected alerts
Gain/loss analysis
Demand/liquidity
```

## Fase 8 — Roblox

```text
Market boards
Item prices
Realtime updates
Trading tools
Portfolio
```

---

# 18. Arquitectura resumida para presentación

```text
                           ROBLOX
                              │
                              ▼
                   ┌────────────────────┐
                   │  LIRO INGESTION    │
                   │                    │
                   │ Discovery          │
                   │ Market             │
                   │ Inventory          │
                   └─────────┬──────────┘
                             │
                             ▼
                   ┌────────────────────┐
                   │     LIRO CORE      │
                   │                    │
                   │ Catalog            │
                   │ Market Data        │
                   │ Valuation          │
                   │ Trading            │
                   │ Portfolio          │
                   └─────────┬──────────┘
                             │
                   ┌─────────┴─────────┐
                   ▼                   ▼
              PostgreSQL             Valkey
                   │                   │
                   └─────────┬─────────┘
                             ▼
                   ┌────────────────────┐
                   │   LIRO PLATFORM    │
                   │                    │
                   │ API                │
                   │ SignalR            │
                   │ Auth               │
                   │ Realtime           │
                   └─────────┬──────────┘
                             │
             ┌───────────────┼───────────────┐
             ▼               ▼               ▼
        LIRO WEB        LIRO EXTENSION   LIRO ROBLOX
         Angular          TypeScript         Luau
```

---

# 19. Idea central

Liro no será:

```text
una web
+
una extensión
+
un juego
```

independientes.

Será:

```text
                   LIRO PLATFORM
                         │
                  única fuente de
                  datos y análisis
                         │
             ┌───────────┼───────────┐
             ▼           ▼           ▼
            Web       Extension    Roblox
```

Esa será la base arquitectónica del proyecto.
