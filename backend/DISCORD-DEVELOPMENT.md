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
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Install
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Configure
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action InstallPanel
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Run
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
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action RegisterStartup
```

No activa Docker ni Liro al iniciar sesion; eso ocurre al pulsar el boton cuando
hace falta. No usa una cuenta administrativa ni guarda la contrasena Windows.

## Funcionamiento

- Solo acepta botones del mensaje, canal y servidor configurados y usuarios con
  el rol autorizado. Los datos de conexión son un mensaje normal en ese canal.
  Se conservan al cerrar Discord o el agente, se actualizan sin duplicarse al
  reiniciar el backend y solo se eliminan tras un Stop confirmado. El identificador
  del mensaje se guarda en connection-message.json; no se guardan allí URLs ni tokens.
- `Iniciar / reiniciar API` reinicia el backend si ya está activo y devuelve la URL
  cuando la API está lista. `Stop` detiene el backend y conserva Docker.
- El mensaje compartido muestra un icono y texto de estado: verde para API lista,
  rojo para detenido, amarillo para transiciones, naranja para API no disponible y
  blanco para estado sin confirmar. Todos ven el mismo estado; no depende de quién pulsó.
- Comprueba AppHost y API con un temporizador de 15 segundos y al ejecutar acciones.
  Edita el mismo mensaje con el estado y la hora de comprobación. Una consulta lenta
  puede retrasar una vuelta; no se superponen comprobaciones. Un fallo no conserva
  indefinidamente el estado «reiniciando»: se muestra estado sin confirmar.
  Si el agente se desconecta, no puede actualizar Discord: revisar la fecha visible.
- Al arrancar el agente actualiza el panel existente y deja solo los dos botones;
  no hace falta volver a publicar el mensaje ni reiniciar el backend.
- Si hace falta, espera Docker o abre Desktop si está cerrado, luego inicia Liro.
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

Probar primero con API LAN ya lista. Pulsar el botón desde una cuenta con rol y
confirmar que reinicia y devuelve la URL cuando está disponible. Abrir esa URL desde otro
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
.\scripts\discord\Setup-DiscordLocalControl.ps1 -Action Uninstall
```

Retira la tarea de inicio y detiene solo el ejecutable del agente instalado en su
ruta local. Conserva configuracion, token cifrado y archivos; no toca Docker,
AppHost, base de datos, firewall ni repositorio. Para actualizar, retirar el agente,
ejecutar Install y Run y volver a registrar el inicio si se desea.

Para rotar token, restablecerlo en el portal Discord y ejecutar Configure con el
agente detenido. Reinstalar el panel explicitamente despues de reconfigurar.
No copiar token.dpapi a otros usuarios: DPAPI lo vincula al usuario Windows.

## Panel Aspire del propietario

Ya no hay botón de Aspire. Con `ShareAspireInChannel=true`, autorizado expresamente
para este canal privado, los datos de API y el enlace de Aspire se publican juntos
en el mensaje persistente. Todos los que puedan leer ese canal tendrán el enlace.
Se mantienen las validaciones de servidor, canal, mensaje y rol para los controles.
Con false (valor predeterminado), el mensaje del canal contiene solo la API y Aspire
se entrega al propietario en una respuesta privada de interacción.

Los datos no repiten el estado del panel ni las indicaciones de red; incluyen un
enlace de consulta con el artículo 97886188. Los mensajes antiguos efímeros no se
pueden convertir en persistentes. Si la API ya está lista al iniciar el agente,
la primera comprobación correcta crea o actualiza el mensaje sin reiniciar el backend.

El estado reutiliza la dirección validada si permanece en la misma interfaz y
subred local. No ejecuta dos veces PowerShell ni sondea dos veces la API por vuelta.
Los inicios y reinicios conservan la validación completa de red y firewall.
La ejecución de `Get-LanConfiguration.ps1` dispone de 30 segundos para cargar
PowerShell y comprobar red/firewall. Los comandos de Docker mantienen 10 segundos
y `aspire stop`, 60. La cancelación del arranque sigue teniendo prioridad; no se
reintenta ni se abre otro proceso por ampliar este margen. El registro local
añade `NetworkCheckStarted`, `NetworkCheckCompleted`, `NetworkCheckFailed` y
`NetworkCheckTimedOut` sin incluir la salida de los comandos ni credenciales.
El registro distingue PanelUpdated, PanelCheckTimedOut, PanelCheckFailed y
PanelUpdateFailed; no registra URLs ni tokens.

Para actualizar un agente ya configurado, ejecutar `Install` y `Run`. Esto cambia
solo el agente; no reinicia el backend ni Docker. Para cambiar el propietario,
usar `ConfigureOwner` con el agente detenido.
`ConfigureOwner` pide el ID de usuario Discord y lo guarda fuera del repositorio;
0 desactiva el acceso. No cambia el token del bot. Ejecutar `InstallPanel` actualiza
los botones del mensaje existente. Una reconfiguracion completa con `Configure`
restablece esta opcion; configurar otra vez el propietario si se necesita.

La respuesta de inicio consulta `aspire ps --format Json` de la cuenta Windows y selecciona solo
el AppHost de BackendPath, activo, con una unica URL de login HTTPS en loopback.
No conserva tokens anteriores ni lee logs de Aspire. Si CLI no encuentra la sesion,
no entrega un token. El enlace contiene el token de Aspire, no el token del bot.
Abrirlo desde la PC anfitriona; localhost desde otra PC apunta a esa otra PC.
No se abre el dashboard a la LAN ni se desactiva su autenticacion.

Para ver el panel no hace falta arrancar Visual Studio. Si Liro ya fue iniciado
por el agente, iniciar otro AppHost puede causar conflictos de puertos. Para
inspeccionar codigo del proceso existente se puede adjuntar el depurador al proceso
correspondiente; para iniciar desde Visual Studio hay que detener antes la instancia
existente. La consulta del enlace de Aspire no inicia una segunda instancia.
