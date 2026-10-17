[CmdletBinding()]
param(
    [ValidateSet('Install','Configure','ConfigureOwner','InstallPanel','StopAgent','Run','RegisterStartup','UnregisterStartup','Uninstall')]
    [string]$Action = 'Install'
)
$ErrorActionPreference = 'Stop'
$localDirectory = Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent'
$appDirectory = Join-Path $localDirectory 'app'
$executable = Join-Path $appDirectory 'Liro.DevelopmentAgent.exe'
$taskName = 'Liro.DevelopmentAgent'

function Get-AgentStartupTask {
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    if ($task) {
        $actions = @($task.Actions)
        if ($actions.Count -ne 1 -or $actions[0].Execute -ne $executable) {
            throw 'La tarea Liro.DevelopmentAgent no corresponde al agente instalado. No se modifico.'
        }
    }
    return $task
}

function Get-AgentVerifiedPath {
    param($AgentProcess)
    $imagePath = $null
    try { $imagePath = $AgentProcess.Path } catch { }
    if ($imagePath) { return $imagePath }
    $nativeError = 'no disponible'
    try {
        $imagePath = & (Join-Path $PSScriptRoot 'Get-AgentProcessImage.ps1') -ProcessId $AgentProcess.Id
        if ($imagePath) { return $imagePath }
    } catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [System.ComponentModel.Win32Exception]) { $nativeError = [string]$cause.NativeErrorCode }
    }
    try {
        $record = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $AgentProcess.Id) -ErrorAction Stop
        if ($record -and $record.ExecutablePath) { return $record.ExecutablePath }
    } catch { }
    throw "No se pudo verificar el ejecutable del PID $($AgentProcess.Id) (consulta Windows: $nativeError). No se detuvo ese proceso. Si el agente fue iniciado como administrador, repite Install desde PowerShell como administrador con el mismo usuario; luego usa Run desde una consola normal."
}

function Stop-InstalledAgent {
    $task = Get-AgentStartupTask
    if ($task) { Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop }
    foreach ($process in @(Get-Process -Name 'Liro.DevelopmentAgent' -ErrorAction SilentlyContinue)) {
        if ($process.HasExited) { continue }
        $imagePath = Get-AgentVerifiedPath -AgentProcess $process
        if ($imagePath -eq $executable) {
            Stop-Process -InputObject $process -Force -ErrorAction Stop
            if (-not $process.WaitForExit(10000)) { throw 'El agente no termino en 10 segundos. No se puede instalar todavia.' }
        }
    }
    $remaining = @(Get-Process -Name 'Liro.DevelopmentAgent' -ErrorAction SilentlyContinue | Where-Object {
        -not $_.HasExited -and (Get-AgentVerifiedPath -AgentProcess $_) -eq $executable
    })
    if ($remaining.Count -gt 0) { throw 'El agente sigue activo o volvio a iniciarse. Se cancelo la operacion.' }
}

if ($Action -eq 'StopAgent') {
    Stop-InstalledAgent
    Write-Host 'Agente detenido y salida verificada. Backend, Docker y configuracion conservados.'
    return
}
if ($Action -eq 'Install') {
    $task = Get-AgentStartupTask
    $restoreStartup = $task -and $task.Settings.Enabled
    try {
        # Prevent scheduled restart while replacing the installed binaries.
        if ($restoreStartup) { Disable-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop | Out-Null }
        Stop-InstalledAgent
        $project = Join-Path $PSScriptRoot 'src\Liro.DevelopmentAgent\Liro.DevelopmentAgent.csproj'
        dotnet publish $project --configuration Release --self-contained false --output $appDirectory
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el agente local.' }
    }
    finally {
        if ($restoreStartup) { Enable-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop | Out-Null }
    }
    Write-Host 'Agente instalado. Si ya esta configurado, ejecuta InstallPanel y luego Run.'
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
    'ConfigureOwner' { & $executable configure-owner; if ($LASTEXITCODE -ne 0) { throw 'No se pudo configurar el propietario.' } }
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

