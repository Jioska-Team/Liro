# Desarrollo del frontend por red local

El servidor ejecuta Aspire y Docker; el programador de frontend solo ejecuta Angular.
El modo LAN esta desactivado por defecto y solo admite Development.

## Activar

1. Detener la instancia anterior de Liro para liberar los puertos.
2. Mantener Docker Desktop iniciado. Usar una red privada de confianza.
3. Desde PowerShell **como administrador**, en la carpeta backend, ejecutar una vez:

   ```powershell
   .\Start-Lan.ps1 -ConfigureFirewall
   ```

4. Desde PowerShell **normal**, en la misma carpeta:

   ```powershell
   .\Start-Lan.ps1
   ```

El script obtiene la IPv4 y mascara de la interfaz privada con puerta de enlace.
Si hay varias, consultar `Get-NetIPConfiguration` y pasar `-InterfaceIndex NUMERO`
en ambos comandos. No cambia el perfil de red ni desactiva el firewall.

Compartir la URL que imprime el script por un canal privado. Configurar la URL base
de Angular localmente; no incorporarla a archivos versionados. Angular se abre en
`http://localhost:4200` en el equipo de cada desarrollador.

Prueba desde el otro equipo: consultar `GET /api/v1/items/by-asset/ID` con un ID
existente. Un 404 puede indicar un articulo inexistente; un timeout indica revisar
el firewall, la IP o el aislamiento entre clientes del Wi-Fi. El historial esta en
`GET /api/v1/items/{id}/market-history`. No se publica el documento OpenAPI por LAN.

## Limites de acceso

- Solo el puerto HTTP 5205 se enlaza a la IPv4 seleccionada, sin proxy de Aspire.
- Solo se aceptan conexiones de esa subred IPv4; X-Forwarded-For no concede acceso.
- Solo GET, HEAD y OPTIONS bajo `/api/v1/items`. Las escrituras, diagnosticos,
  health checks y OpenAPI quedan bloqueados por ese puerto.
- CORS permite el origen exacto de Angular y los metodos de lectura, sin cookies
  ni cabeceras administrativas. CORS no sustituye al firewall ni autentica personas.
- Dashboard, PostgreSQL, Valkey y HTTPS conservan su configuracion local.
- HTTP no cifra los datos: este modo es para el catalogo publico en una LAN de
  confianza. No usarlo para login, tokens, contrasenas o informacion privada.
  Para esos casos se requiere HTTPS con un certificado valido en ambos equipos.

## Repositorio y configuracion

El script no escribe IP, subred ni secretos en archivos. Usa variables de entorno
del proceso y restaura sus valores al terminar. No necesita compartir claves de
administracion ni cadenas de conexion. La URL sera visible para quienes consumen
la API; ocultar el codigo o la IP no es un control de acceso.

La regla de firewall permanece en Windows, restringida al perfil privado,
interfaz, IP y subred del momento de creacion. Si cambia la IP o la red, quitarla
y crearla de nuevo. Otras reglas amplias existentes pueden permitir conexiones,
pero la API sigue comprobando la subred y los metodos.

Para desactivar: CTRL+C y arrancar Liro normalmente, sin este script.
Para quitar la regla (PowerShell como administrador):

```powershell
.\Start-Lan.ps1 -RemoveFirewall
```

No abrir puertos en el router. No compartir el enlace con token del Dashboard.
