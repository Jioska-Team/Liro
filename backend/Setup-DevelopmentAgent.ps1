[CmdletBinding()]
param(
    [ValidateSet('Install','Configure','InstallPanel','Run','RegisterStartup','UnregisterStartup','Uninstall')]
    [string]$Action = 'Install'
)
$ErrorActionPreference = 'Stop'
$localDirectory = Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent'
$appDirectory = Join-Path $localDirectory 'app'
$executable = Join-Path $appDirectory 'Liro.DevelopmentAgent.exe'
$taskName = 'Liro.DevelopmentAgent'

if ($Action -eq 'Install') {
    $project = Join-Path $PSScriptRoot 'src\Liro.DevelopmentAgent\Liro.DevelopmentAgent.csproj'
    dotnet publish $project --configuration Release --self-contained false --output $appDirectory
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el agente local.' }
    Write-Host 'Agente instalado. Ejecuta las acciones Configure e InstallPanel; luego Run.'
    return
}
if ($Action -in @('UnregisterStartup','Uninstall')) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    if ($Action -eq 'Uninstall') {
        Get-Process -Name 'Liro.DevelopmentAgent' -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Path -eq $executable) { Stop-Process -Id $_.Id }
        }
        Write-Host 'Inicio automatico retirado y agente local detenido. Configuracion cifrada y archivos conservados. Docker y Liro no se han detenido.'
    }
    return
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Ejecuta primero la accion Install.' }
switch ($Action) {
    'Configure' { & $executable configure; if ($LASTEXITCODE -ne 0) { throw 'No se pudo configurar el agente.' } }
    'InstallPanel' { & $executable install-panel; if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el panel.' } }
    'Run' { Start-Process -FilePath $executable -ArgumentList 'run' -WindowStyle Hidden | Out-Null }
    'RegisterStartup' {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $actionDefinition = New-ScheduledTaskAction -Execute $executable -Argument 'run' -WorkingDirectory $appDirectory
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $taskName -Action $actionDefinition -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        Write-Host 'Inicio al iniciar sesion configurado para el usuario actual, sin elevar privilegios.'
    }
}
