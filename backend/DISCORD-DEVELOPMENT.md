# Boton de Discord para iniciar Liro

El agente se ejecuta en tu PC Windows independientemente de Docker y Aspire. No
despliega nada a Internet: la URL que devuelve sigue siendo accesible solo por LAN.
Requiere PC encendida, sesion iniciada e Internet. No despierta un PC suspendido.

## Preparacion local

1. Instalar .NET 10 y Docker Desktop con contenedores Linux. Completar manualmente
   su primera ejecucion y comprobar que el contexto `desktop-linux` usa un motor
   local. El agente no instala componentes ni acepta condiciones en tu nombre.
2. Configurar la regla LAN una vez con `Start-Lan.ps1 -ConfigureFirewall` desde
   PowerShell como administrador. El resto se ejecuta como usuario normal.
3. En Discord Developer Portal crear una aplicacion y su bot. Mantener desactivados
   los privileged gateway intents; no configurar Interactions Endpoint URL.
4. Invitar el bot al servidor mediante OAuth2 URL Generator, scope `bot`, permisos
   View Channels, Send Messages y Read Message History en el canal de desarrollo.
   No conceder Administrator. Crear un rol para las personas autorizadas y un canal
   privado que solo puedan ver esas personas y el bot.
5. Activar Developer Mode en Discord para copiar los IDs de servidor, canal y rol.
   El Application ID aparece en el portal. El rol autorizado es el de las personas,
   no el rol del bot.

## Instalar y configurar

Desde PowerShell normal en la carpeta backend:

```powershell
.\Setup-DevelopmentAgent.ps1 -Action Install
.\Setup-DevelopmentAgent.ps1 -Action Configure
.\Setup-DevelopmentAgent.ps1 -Action InstallPanel
.\Setup-DevelopmentAgent.ps1 -Action Run
```

Configure solicita la ruta absoluta de backend, IDs, interfaz opcional y token
mediante entrada oculta. No pegar el token en el chat, comandos ni archivos del
repositorio. Se cifra con DPAPI para tu usuario Windows. Si hay varias interfaces
privadas, consultar `Get-NetIPConfiguration` y escoger el indice correcto.

InstallPanel solicita escribir PUBLICAR antes de enviar el mensaje con el boton.
Reutiliza el ID guardado cuando el mensaje ya existe. No crea paneles al iniciar
el agente. Detener el agente antes de reconfigurar o instalar el panel: todos los
modos toman un bloqueo exclusivo para no escribir configuracion concurrentemente.

Opcional, para arrancar el agente al iniciar sesion:

```powershell
.\Setup-DevelopmentAgent.ps1 -Action RegisterStartup
```

No activa Docker ni Liro al iniciar sesion; eso ocurre al pulsar el boton cuando
hace falta. No usa una cuenta administrativa ni guarda la contrasena Windows.

## Funcionamiento

- Solo acepta botones del mensaje, canal y servidor configurados y usuarios con
  el rol autorizado. Las respuestas con la URL son efimeras.
- Si la API ya responde y sus dependencias estan listas, devuelve la URL.
- En otro caso espera Docker o abre Desktop si esta cerrado, luego inicia Liro.
- No duplica un arranque activo. Si encuentra un AppHost manual, espera su
  disponibilidad; si esta en modo incompatible, pide revisarlo en la PC.
- El arranque tiene limite de 8 minutos y una pausa de 30 segundos tras fallar.
- No reinicia Docker, no mata AppHosts, no cambia firewall ni ejecuta comandos
  recibidos desde Discord. Los helpers se lanzan sin ventanas auxiliares.

Los secretos/configuracion/logs viven bajo `%LOCALAPPDATA%\Liro\DevelopmentAgent`.
Los logs del agente contienen solo codigos de evento, no salida de Aspire ni
excepciones. Los logs propios de Aspire siguen siendo privados del host: no
compartirlos sin retirar el enlace de acceso al Dashboard.

## Prueba manual

Probar primero con API LAN ya lista. Pulsar el boton desde una cuenta con rol y
confirmar que devuelve la URL sin abrir procesos nuevos. Abrir esa URL desde otro
equipo y consultar un articulo existente. Probar con dos pulsaciones, con Liro
detenido, y finalmente con Docker Desktop cerrado. Una cuenta sin rol debe ser
rechazada sin iniciar nada.

El agente comprueba disponibilidad desde la PC anfitriona; eso no prueba que el
Wi-Fi permita conexiones entre equipos. Ante cambios de IP, actualizar la regla
LAN local siguiendo LAN-DEVELOPMENT.md. No abrir puertos en el router.

Si el boton no responde, comprobar que el agente esta ejecutandose, su configuracion
y el token. Si el motor no inicia, revisar Docker Desktop directamente. Si Liro
no arranca, ejecutarlo manualmente para ver su diagnostico. Un proceso de estado
desconocido bloquea nuevos arranques; no se asume que esta apagado.

## Retirar o actualizar

```powershell
.\Setup-DevelopmentAgent.ps1 -Action Uninstall
```

Retira la tarea de inicio y detiene solo el ejecutable del agente instalado en su
ruta local. Conserva configuracion, token cifrado y archivos; no toca Docker,
AppHost, base de datos, firewall ni repositorio. Para actualizar, retirar el agente,
ejecutar Install y Run y volver a registrar el inicio si se desea.

Para rotar token, restablecerlo en el portal Discord y ejecutar Configure con el
agente detenido. Reinstalar el panel explicitamente despues de reconfigurar.
No copiar token.dpapi a otros usuarios: DPAPI lo vincula al usuario Windows.
