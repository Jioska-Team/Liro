# Sandbox privado de Liro

Preparación para Staging. No instala ni controla el bot local, Aspire de desarrollo ni Discord.Alerts. Los recursos se crean bajo el proyecto Compose `liro-sandbox`, sin referencias a contenedores o volúmenes externos.

## Qué arranca

- PostgreSQL 17: base y usuario `liro_sandbox`, volumen propio, sin puerto publicado.
- Valkey 8: volumen propio, sin puerto publicado, AOF y política noeviction para no descartar bloqueos/presupuestos por presión de memoria.
- `Liro.SandboxMigrator`: aplica migraciones EF exclusivamente al destino sandbox esperado y termina. No ejecuta servicios de inicialización del catálogo. La API depende de su finalización correcta.
- API Staging: puerto **127.0.0.1:15205**, `/health` (dependencias) y `/alive` (proceso), sin modo LAN.

La red `data` es interna: no da salida a Roblox ni a proxies. Los servicios catálogo y mercado están declarados en el perfil `workers`, desactivado por defecto; sus tareas de consulta también quedan desactivadas. Incluso si se inicia el perfil, la red sigue aislada. Las tareas de Outbox/componentes que no tienen interruptor permanecen en el host de catálogo; por eso la red, los perfiles y la base vacía son parte del aislamiento.

No se importan datos, CatalogoGeneral, proxies, sesiones ni claves del entorno activo. La API inicia vacía; las operaciones que requieran Roblox no funcionarán en este modo aislado. Las consultas de datos locales sí podrán probarse. No incluye frontend ni habilita CORS remoto.

## Preparación de secretos

Copiar `sandbox.env.example` a un directorio privado fuera del repositorio. Generar dos valores hexadecimales aleatorios de 64 caracteres diferentes, uno para SANDBOX_DB_PASSWORD y otro para SANDBOX_ADMIN_API_KEY. En PowerShell 7 puede generarse cada valor con:

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Guardar los valores en el archivo privado, no en consola compartida ni Git. No reutilizar credenciales del entorno local. El script acepta únicamente estas variables y el puerto 15205. No imprimir `docker compose config` sin `--quiet`: su salida incluye valores resueltos.

## Validación y arranque explícito

Requisitos del servidor: Docker con Compose v2, acceso al registro de imágenes y capacidad libre. Esta preparación no certifica CPU/RAM del servidor; los límites configurados suman aproximadamente 2 GiB para el arranque básico incluyendo el migrador y no incluyen Docker, sistema operativo ni compilación.

Desde una sesión normal autorizada, usando la ruta real de tu archivo privado:

```powershell
& 'D:\Proyectos\Liro\backend\scripts\sandbox\Manage-Sandbox.ps1' -Action Validate -EnvFile 'D:\LiroSecrets\sandbox.env'
& 'D:\Proyectos\Liro\backend\scripts\sandbox\Manage-Sandbox.ps1' -Action Build -EnvFile 'D:\LiroSecrets\sandbox.env'
& 'D:\Proyectos\Liro\backend\scripts\sandbox\Manage-Sandbox.ps1' -Action Start -EnvFile 'D:\LiroSecrets\sandbox.env'
& 'D:\Proyectos\Liro\backend\scripts\sandbox\Manage-Sandbox.ps1' -Action Status -EnvFile 'D:\LiroSecrets\sandbox.env'
```

`Start` espera salud de PostgreSQL/Valkey, migraciones y API. No inicia workers ni Discord. Para detener, usar la acción `Stop`, que conserva volúmenes. Los comandos están fijados al proyecto `liro-sandbox` y no usan nombres del backend actual. La compilación puede competir por recursos si se hace en el mismo host activo; ejecutarla en el servidor de pruebas o durante una ventana apropiada.

Comprobar desde el servidor:

```text
http://127.0.0.1:15205/health
http://127.0.0.1:15205/alive
http://127.0.0.1:15205/api/v1/items
```

Acceso remoto mediante túnel privado; no cambiar el enlace a 0.0.0.0 para exponerlo. Si se necesita acceso compartido, preparar antes TLS, autenticación de acceso y orígenes del frontend. Los health checks no reinician por sí mismos un proceso vivo marcado unhealthy: `restart` recupera salidas, y los estados unhealthy requieren diagnóstico. Los logs están limitados por tamaño y rotación.

## Cambios de versión y respaldo

Antes de aplicar nuevas migraciones a una base ya poblada, crear y verificar un respaldo propio del sandbox. Ejemplo con Compose y tu archivo privado:

```text
docker compose --env-file /secure/sandbox.env -p liro-sandbox -f deploy/sandbox/compose.yaml exec -T sandbox-postgres pg_dump -U liro_sandbox -d liro_sandbox -Fc -f /tmp/liro-sandbox.dump
docker compose --env-file /secure/sandbox.env -p liro-sandbox -f deploy/sandbox/compose.yaml cp sandbox-postgres:/tmp/liro-sandbox.dump /secure/backups/liro-sandbox.dump
```

Al actualizar el esquema, detener API/workers, reconstruir y recrear el migrador antes de volver a arrancar. No asumir que reiniciar la API vuelve a ejecutar un contenedor migrador ya completado. No automatizamos migraciones hacia atrás ni borrado de volúmenes. Para revertir un cambio incompatible, restaurar el respaldo del sandbox y la versión anterior de sus imágenes, nunca tocar los volúmenes locales activos. Conservar etiquetas/digests de las imágenes probadas antes de actualizar; los tags de línea mayor reciben parches.

## Consultas reales y alertas

Activar catálogo/mercado con tráfico real es un paso posterior explícito: habilitar tareas/perfil, proporcionar una red de salida, claves y proxies propios y cargar solo la configuración necesaria. Un Valkey separado no coordina presupuestos con el entorno actual; compartir IP de salida/proxies puede afectar sus límites. No se suministra un override que active automáticamente ese tráfico.

Discord.Alerts conserva su despliegue independiente en `deploy/discord-alerts` y debe usar su bot/canal Staging y volumen propio. Una API key distinta de Webshare en la misma cuenta no crea una cuota separada. LocalControl queda exclusivamente en el equipo de desarrollo.

## Alcance de la validación

Se validan configuración Compose, aislamiento declarado, compilación y pruebas sin conectar bases activas. La construcción de imágenes, migración de una base nueva, acceso HTTP real y persistencia tras recreación deben verificarse en un motor Docker autorizado antes de considerar el sandbox desplegado.

Referencias: [dependencias y migración previa](https://docs.docker.com/compose/how-tos/startup-order/), [red interna](https://docs.docker.com/reference/compose-file/networks/), [perfiles](https://docs.docker.com/compose/how-tos/profiles/).
