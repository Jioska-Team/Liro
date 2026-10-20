# Scripts operativos

> Actualización 2026-10-19: el bot instalado ya usa `scripts/development`. Se retiraron los siete enlaces `.ps1` de la raíz después de verificar su conexión. Usar exclusivamente las rutas bajo `scripts/`. Las menciones anteriores a enlaces compatibles describen el estado previo.


Las implementaciones están organizadas por responsabilidad:

```text
scripts/
  sandbox/
    Manage-Sandbox.ps1         # validar/compilar/iniciar/consultar/detener solo liro-sandbox
  discord/
    Setup-DiscordLocalControl.ps1
    Get-AgentProcessImage.ps1
    Enable-WebshareAlerts.ps1   # entrada obsoleta: remite al despliegue independiente
  development/
    Start-Lan.ps1
    Get-LanConfiguration.ps1
  verification/
    Verify.ps1
```

Las rutas originales de la raíz siguen siendo enlaces compatibles que reenvían parámetros. `Setup-DevelopmentAgent.ps1` también redirige al instalador actual. No contienen otra implementación. Los binarios del bot local siguen utilizando esas entradas estables; no requieren reinstalación por esta reorganización.

Los scripts resuelven la raíz backend a partir de su ubicación. El script de instalación conserva ejecutable, tarea de Windows, directorio de configuración y credenciales. No ejecutar Install para aplicar una mera reorganización del código.

Las pruebas de PowerShell permanecen en `tests/Liro.Discord.LocalControl.RegressionTests/Scripts.Tests.ps1`. La suite prueba ahora las implementaciones nuevas; también se verificaron las entradas anteriores con operaciones de Windows simuladas.

`scripts/development/Start-Lan.ps1` es para desarrollo local; no es un despliegue de Staging. Las alertas independientes se despliegan según `docs/discord-alerts.md`.
