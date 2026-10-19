# Liro.Discord.Alerts

Servicio independiente para alertas Webshare al 70%, 85% y 95%. Compatible con Windows y Linux (.NET 10). No controla el backend, no consulta Roblox y no usa los proxies, PostgreSQL ni Valkey. No se registra en AppHost: tiene su propio ciclo de vida.

## Arquitectura

- `Liro.Application/Alerts`: política de umbrales, estado pendiente, deduplicación por ciclo y puertos de persistencia, consulta y entrega.
- `Liro.Infrastructure/Alerts`: consulta HTTP de Webshare y almacenamiento JSON atómico.
- `Liro.Discord.Alerts`: composición de dependencias, configuración, servicio hospedado y adaptador Discord.
- `Liro.Discord.LocalControl`: controles locales existentes; ya no inicia alertas ni guarda nuevas claves Webshare.

Se mantienen las comprobaciones cada cinco minutos, tres consultas a Webshare por comprobación, timeout y Retry-After. Un salto directo al 95% emite el aviso más alto. La cuota es la real del ciclo, no una proyección. Los errores no se convierten en consumo cero.

## Configuración por ambiente

Crear una aplicación/bot Discord independiente del bot local. Para Staging y Production usar bots y canales diferentes. En el canal privado otorgar al bot View Channel, Send Messages y Read Message History; no necesita Administrator ni permisos para controlar procesos. Configurar el rol que verá el canal desde Discord.

Inyectar estas variables con el gestor de secretos del despliegue, sin guardarlas en Git:

```text
DOTNET_ENVIRONMENT=Staging
Alerts__ApplicationId=<ID de la aplicación del bot de alertas>
Alerts__GuildId=<ID del servidor Discord>
Alerts__ChannelId=<ID del canal de alertas>
Alerts__BotToken=<token del nuevo bot>
Alerts__WebshareApiKey=<API key de Webshare>
Alerts__StateDirectory=/var/lib/liro-alerts
```

Para producción cambiar el ambiente a `Production` y sus credenciales/canal. En Windows usar una ruta absoluta, por ejemplo `D:\LiroData\DiscordAlerts`. El proceso debe poder escribir allí. Nunca reutilizar el token de LiroLocal. Los secretos DPAPI del agente local no se copian ni se reutilizan automáticamente.

## Publicación y ejecución

Desde `backend`:

```powershell
dotnet publish src/Liro.Discord.Alerts/Liro.Discord.Alerts.csproj -c Release -o ./publish/discord-alerts
dotnet ./publish/discord-alerts/Liro.Discord.Alerts.dll
```

El servidor debe supervisar este ejecutable con reinicio ante fallo y parada ordenada. Para contenedores se incluye `deploy/discord-alerts/compose.yaml`; construir desde la raíz backend y proporcionar un archivo de entorno fuera del repositorio:

```text
docker compose --env-file /secure/liro-alerts.env -f deploy/discord-alerts/compose.yaml -p liro-alerts-staging up -d --build
```

Usar otro nombre de proyecto Compose para producción y no escalar réplicas. El volumen conserva el estado durante recreaciones. El bloqueo impide dos procesos sobre el mismo directorio; no es un bloqueo distribuido entre servidores con discos distintos. Ejecutar una sola réplica por bot/canal y conservar su volumen al migrar de servidor.

## Estado y diagnóstico

Se guarda bajo `StateDirectory/<ambiente>/<ApplicationId>/<ChannelId>/`:

- `bandwidth-alerts-<canal>.json`: ciclos y envíos pendientes/confirmados, hasta 32 ciclos.
- `bandwidth-health.json`: estado, última comprobación, última comprobación exitosa y consumo observado.
- `alerts.lock`: exclusión de instancia, liberada por el sistema al finalizar el proceso.

Supervisar también la antigüedad de `CheckedAt`: un archivo Healthy antiguo no prueba que el proceso siga vivo. Los errores de autenticación, cuota y entrega quedan como estado fallido y se reintentan con espera. No se registran tokens ni cuerpos de respuestas. Si un envío queda pendiente, se busca su marcador entre los últimos 100 mensajes antes de reenviar; si ese historial no alcanza para resolverlo, se informa el fallo sin reenviar a ciegas.

## Migración del agente local

El código local ya no ejecuta el monitor. No se modifica ni reinicia su instalación en ejecución. Antes de activar el nuevo servicio, comprobar que una instalación antigua no siga enviando alertas al mismo destino. En esta instalación las alertas anteriores no se habían activado con una API key.

Si existiesen avisos ya enviados por otro bot, no copiar pendientes a ciegas: la conciliación verifica el autor. Mantener el nuevo canal exclusivo y tratar la activación como un nuevo destino de alertas. Los scripts antiguos `Enable-WebshareAlerts.ps1` y `configure-webshare` señalan esta documentación sin reinstalar el bot local.

No se realiza depuración de historial de mercado ni cambios de frecuencias o límites.
