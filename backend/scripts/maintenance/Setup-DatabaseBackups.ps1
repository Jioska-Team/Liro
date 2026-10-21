#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigPath, [string]$TaskName = 'Liro.DatabaseBackup')
$ErrorActionPreference = 'Stop'
$configFile = (Resolve-Path -LiteralPath $ConfigPath).Path
$scriptFile = Join-Path $PSScriptRoot 'Backup-Database.ps1'
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
$action = New-ScheduledTaskAction -Execute $pwsh -Argument ('-NoProfile -NonInteractive -File "' + $scriptFile + '" -ConfigPath "' + $configFile + '"')
$trigger = New-ScheduledTaskTrigger -Daily -At '03:00'
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Select-Object TaskName, State
