# Liro Web

Aplicación Angular con SSR configurado. La interfaz actual consulta el catálogo paginado
y los componentes de bundles mediante `src/app/core/api/catalog.ts`; no representa todavía
todos los módulos del diseño global.

## Desarrollo

Desde `web/liro-web/`, con Node compatible con los requisitos de
[package-lock.json](package-lock.json) (en la rama 24, 24.15 o posterior):

```powershell
npm ci
npm start -- --proxy-config proxy.conf.json
```

Abrir `http://localhost:4200`. [proxy.conf.json](proxy.conf.json) debe apuntar al endpoint
real de la API local; su destino versionado es `https://localhost:7174`.
El proxy se pasa explícitamente porque `angular.json` no lo configura por defecto.
Para consumir el backend de otro equipo, seguir la [guía LAN y contrato HTTP](../../backend/docs/frontend/README.md).
No incorporar claves administrativas en Angular.

## Comprobaciones

- `npm run build`: compilación.
- `npm test`: pruebas configuradas con Vitest.
- `npm run serve:ssr:liro-web`: ejecutar el servidor después de compilar.

No hay target e2e configurado. Estas instrucciones no implican que las comprobaciones se
hayan ejecutado durante una edición documental.

[package.json](package.json) declara Angular `^22.2.0`, CLI/SSR `^22.2.1`,
TypeScript `~6.0.2` y npm `11.19.0`; el lockfile fija las resoluciones.
SignalR y Lightweight Charts son dependencias instaladas, no evidencia de integración funcional.

La documentación Angular permanece aquí; los contratos del servidor se mantienen en
[backend/docs/frontend](../../backend/docs/frontend/README.md).
