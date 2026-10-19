# Control del backend desde Discord

El panel conserva su mensaje y ofrece estas acciones al rol de desarrollo autorizado:

- **Iniciar / reiniciar API:** si esta detenido, inicia Docker Desktop cuando sea necesario y luego Liro. Si ya esta activo, solicita el cierre de AppHost, espera su detencion y arranca nuevamente. Devuelve la URL solo al comprobar la disponibilidad de la API.
- **Stop:** solicita el cierre de AppHost y sus recursos mediante `aspire stop --apphost <proyecto> --non-interactive`. No usa `--all` ni `--force`, no cierra Docker Desktop ni elimina volumenes. Puede interrumpir una sesion iniciada desde Visual Studio para ese mismo proyecto.
- **Estado:** consulta AppHost y `/api/lan/ready` sin iniciar servicios. Distingue detenido, iniciando, reiniciando, deteniendose, activo/API lista, AppHost activo/API no lista y estado desconocido. Es una instantanea al pulsar; no certifica la salud individual de todos los recursos de Aspire.
- **Mi panel Aspire:** sigue siendo exclusivo del propietario configurado y entrega el enlace de acceso en privado.

Los clics simultaneos para una misma operacion comparten el trabajo. Una operacion incompatible recibe un aviso de ocupado; no queda un reinicio pendiente que se ejecute despues de un Stop. Si el cierre no se confirma, no se inicia otra instancia.

## Respuestas privadas

Cada respuesta autorizada reemplaza la anterior del mismo usuario, incluyendo el enlace privado de Aspire. Las respuestas terminadas se eliminan a los dos minutos; hay un limite de diez minutos desde la respuesta inicial. El panel permanente y los mensajes de otros usuarios no se eliminan. Los identificadores de interaccion permanecen solo en memoria, nunca en configuracion o registros.

Discord limita los tokens de interaccion a 15 minutos. Las respuestas anteriores a esta version, de otra ejecucion del agente o con token vencido no se pueden recuperar para eliminarlas. Un fallo de red o un cierre abrupto del agente tambien puede impedir la limpieza; estas respuestas pueden requerir eliminacion manual.

## Actualizar el agente instalado

Desde PowerShell normal, en la carpeta `backend`, cuando no haya una operacion del bot en curso:

```powershell
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action StopAgent
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Install
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action InstallPanel
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Run
```

En `InstallPanel`, escribir `PUBLICAR` actualiza el mensaje existente. No hace falta volver a configurar IDs o token. StopAgent solo cierra el agente instalado; no detiene el backend. Si el agente se ejecuta como tarea programada y vuelve a iniciarse durante la instalacion, detener esa tarea antes de actualizar.

Requiere Aspire CLI con soporte de `stop --apphost --non-interactive`. Puede comprobarse con `aspire stop --help`. El bot nunca muestra la salida sin filtrar de la CLI. Probar primero Estado; despues Iniciar / reiniciar API y Stop cuando sea aceptable interrumpir el backend compartido.

Referencias: [Aspire stop](https://aspire.dev/reference/cli/commands/aspire-stop/), [respuestas de Discord](https://docs.discord.com/developers/interactions/receiving-and-responding).
