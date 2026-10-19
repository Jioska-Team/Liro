param([string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)
$ErrorActionPreference = 'Stop'
# Mock all OS mutations. These tests must never touch real firewall rules, tasks or processes.
$global:LiroTest_tasks = @{}
$global:LiroTest_stopped = @()
$global:LiroTest_running = $true
$global:LiroTest_publishExit = 0
$global:LiroTest_publishCalls = 0
$global:LiroTest_events = @()
$global:LiroTest_unknownPath = $false
$global:LiroTest_respawn = $false
$global:LiroTest_private = $true
$global:LiroTest_prefix = 24
$global:LiroTest_remoteSubnet = '192.168.50.0/24'
function Assert-True($condition, $message) { if (-not $condition) { throw $message } }
function Get-NetIPConfiguration { [pscustomobject]@{ IPv4DefaultGateway='gateway'; NetProfile=[pscustomobject]@{NetworkCategory=$(if ($global:LiroTest_private) {'Private'} else {'Public'})}; InterfaceIndex=7; InterfaceAlias='TestLAN' } }
function Get-NetIPAddress { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{AddressState='Preferred'; SkipAsSource=$false; IPAddress='192.168.50.10'; PrefixLength=$global:LiroTest_prefix} }
function Get-NetFirewallRule { [CmdletBinding()]param($Name) [pscustomobject]@{Enabled='True'; Action='Allow'; Direction='Inbound'; Profile='Private'} }
function Get-NetFirewallAddressFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { [pscustomobject]@{LocalAddress=@('192.168.50.10'); RemoteAddress=@($global:LiroTest_remoteSubnet)} } }
function Get-NetFirewallPortFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { [pscustomobject]@{LocalPort=@('5205')} } }
function Get-ScheduledTask { [CmdletBinding()]param($TaskName,$TaskPath) if ($global:LiroTest_tasks.ContainsKey($TaskName)) { $global:LiroTest_tasks[$TaskName] } }
function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) [pscustomobject]@{Execute=$Execute; Argument=$Argument} }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn,$User,[switch]$Once,$At,$RepetitionInterval) [pscustomobject]@{User=$User; Once=$Once; RepetitionInterval=$RepetitionInterval} }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) Assert-True ($RunLevel -eq 'Limited' -and $LogonType -eq 'Interactive') 'Elevated task requested'; [pscustomobject]@{User=$UserId} }
function New-ScheduledTaskSettingsSet { param($MultipleInstances,$ExecutionTimeLimit,$RestartCount,$RestartInterval,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,[switch]$StartWhenAvailable) Assert-True ($MultipleInstances -eq 'IgnoreNew') 'Duplicate task instances allowed'; [pscustomobject]@{} }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force) $global:LiroTest_tasks[$TaskName]=[pscustomobject]@{Actions=@($Action);Argument=$Action.Argument;Triggers=@($Trigger);Settings=[pscustomobject]@{Enabled=$true}}  }
function Unregister-ScheduledTask { param($TaskName,[switch]$Confirm) $global:LiroTest_tasks.Remove($TaskName) }
function Start-ScheduledTask { param($TaskName,$TaskPath) $global:LiroTest_events += 'task-start' }
function Start-Process { param($FilePath,$ArgumentList,$WindowStyle) throw 'Inicio fuera de la tarea supervisada' }
function Test-Path { param($LiteralPath,$PathType) $true }
$global:LiroTest_cimPath = $null
function Get-CimInstance { [CmdletBinding()]param($ClassName,$Filter) [pscustomobject]@{ExecutablePath=$global:LiroTest_cimPath} }
function Stop-ScheduledTask { [CmdletBinding()]param($TaskName,$TaskPath) $global:LiroTest_events += 'task-stop' }
function Disable-ScheduledTask { [CmdletBinding()]param($TaskName,$TaskPath) $global:LiroTest_events += 'disable'; $global:LiroTest_tasks[$TaskName].Settings.Enabled=$false }
function Enable-ScheduledTask { [CmdletBinding()]param($TaskName,$TaskPath) $global:LiroTest_events += 'enable'; $global:LiroTest_tasks[$TaskName].Settings.Enabled=$true }
function Get-Process {
    [CmdletBinding()]param($Name)
    if ($global:LiroTest_running -or $global:LiroTest_respawn) {
        $process = [pscustomobject]@{Id=101;Path=$(if ($global:LiroTest_unknownPath) {$null} else {Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent\app\Liro.DevelopmentAgent.exe'})}
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($timeout) $global:LiroTest_events += 'wait'; return $true }
        $process
    }
    [pscustomobject]@{Id=102;Path='C:\Other\Liro.DevelopmentAgent.exe'}
}
function Stop-Process {
    [CmdletBinding()]param($Id,$InputObject,[switch]$Force)
    if ($InputObject) { $Id=$InputObject.Id }
    $global:LiroTest_stopped += $Id
    $global:LiroTest_events += 'process-stop'
    if ($Id -eq 101) { $global:LiroTest_running=$false }
}
function dotnet {
    Assert-True (-not $global:LiroTest_running) 'Publish started while process was running'
    if ($global:LiroTest_tasks.Count -gt 0) { Assert-True (-not $global:LiroTest_tasks['Liro.DevelopmentAgent'].Settings.Enabled) 'Task was not disabled during publish' }
    $global:LiroTest_publishCalls++
    $global:LiroTest_events += 'publish'
    $global:LASTEXITCODE=$global:LiroTest_publishExit
}

$helper = Join-Path $BackendRoot 'scripts/development/Get-LanConfiguration.ps1'
$result = & $helper -CheckFirewall | ConvertFrom-Json
Assert-True ($result.Address -eq '192.168.50.10' -and $result.Subnet -eq '192.168.50.0/24' -and $result.InterfaceIndex -eq 7) 'LAN calculation differs from actual interface'
Write-Output 'PASS LAN resolver selects private interface and exact subnet'
$global:LiroTest_remoteSubnet = '192.168.50.0/255.255.255.0'
$result = & $helper -CheckFirewall | ConvertFrom-Json
Assert-True ($result.Subnet -eq '192.168.50.0/24') 'Equivalent Windows dotted mask rejected'
Write-Output 'PASS firewall accepts equivalent dotted mask'
foreach ($invalid in @('192.168.50.0/255.255.0.0', '192.168.51.0/255.255.255.0', '192.168.50.0/255.0.255.0')) {
    $global:LiroTest_remoteSubnet = $invalid
    $rejected = $false
    try { & $helper -CheckFirewall | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Different or invalid firewall subnet accepted'
}
Write-Output 'PASS firewall rejects different and invalid dotted masks'
$global:LiroTest_remoteSubnet = '192.168.50.0/24'
$global:LiroTest_private = $false
$rejected = $false
try { & $helper | Out-Null } catch { $rejected = $true }
Assert-True $rejected 'Public network accepted'
$global:LiroTest_private = $true
Write-Output 'PASS public network rejected'
$global:LiroTest_prefix = 8
$rejected = $false
try { & $helper | Out-Null } catch { $rejected = $true }
Assert-True $rejected 'Subnet extending outside private range accepted'
$global:LiroTest_prefix = 24
Write-Output 'PASS invalid subnet rejected'
$global:LiroTest_remoteSubnet = 'Any'
$rejected = $false
try { & $helper -CheckFirewall | Out-Null } catch { $rejected = $true }
Assert-True $rejected 'Broad firewall scope accepted'
Write-Output 'PASS firewall mismatch rejected'

$setup = Join-Path $BackendRoot 'scripts/discord/Setup-DiscordLocalControl.ps1'
& $setup -Action RegisterStartup
& $setup -Action RegisterStartup
Assert-True ($global:LiroTest_tasks.Count -eq 1 -and $global:LiroTest_tasks['Liro.DevelopmentAgent'].Argument -eq 'run') 'Repeated install duplicated startup task'
Write-Output 'PASS repeated startup registration is idempotent and unprivileged'
Assert-True (@($global:LiroTest_tasks['Liro.DevelopmentAgent'].Triggers | Where-Object {$_.Once -and $_.RepetitionInterval -eq [TimeSpan]::FromMinutes(1)}).Count -eq 1) 'Falta recuperación periódica cada minuto'
$global:LiroTest_events=@()
& $setup -Action Run
& $setup -Action Run
Assert-True (@($global:LiroTest_events | Where-Object {$_ -eq 'task-start'}).Count -eq 2 -and $global:LiroTest_tasks.Count -eq 1) 'Run no reutiliza la tarea supervisada'
Write-Output 'Correcto: recuperación cada minuto e inicio supervisado sin tareas duplicadas'
& $setup -Action StopAgent
Assert-True ($global:LiroTest_tasks.Count -eq 1 -and $global:LiroTest_stopped.Count -eq 1 -and $global:LiroTest_stopped[0] -eq 101) 'StopAgent touched another process or startup task'
Assert-True (-not $global:LiroTest_tasks['Liro.DevelopmentAgent'].Settings.Enabled) 'StopAgent permite que el agente reaparezca'
Write-Output 'PASS StopAgent preserves startup task and stops only installed agent'
$global:LiroTest_stopped = @()
$global:LiroTest_running = $true
& $setup -Action Uninstall
Assert-True ($global:LiroTest_tasks.Count -eq 0 -and $global:LiroTest_stopped.Count -eq 1 -and $global:LiroTest_stopped[0] -eq 101) 'Uninstall touched a foreign process'
Write-Output 'PASS uninstall only stops exact installed agent and removes its task'
& $setup -Action RegisterStartup
$global:LiroTest_running=$true
$global:LiroTest_events=@()
& $setup -Action Install
Assert-True (($global:LiroTest_events -join ',') -eq 'disable,task-stop,process-stop,wait,publish,enable') 'Install did not disable-stop-wait-publish-restore in order'
Write-Output 'PASS Install waits for exit and disables scheduled restart during publish'
$global:LiroTest_publishExit=1
$rejected=$false
try { & $setup -Action Install } catch { $rejected=$true }
Assert-True ($rejected -and $global:LiroTest_tasks['Liro.DevelopmentAgent'].Settings.Enabled) 'Publish failure did not restore task state'
Write-Output 'PASS failed install restores scheduled task state'
$global:LiroTest_publishExit=0
$global:LiroTest_tasks['Liro.DevelopmentAgent'].Settings.Enabled=$false
& $setup -Action Install
Assert-True (-not $global:LiroTest_tasks['Liro.DevelopmentAgent'].Settings.Enabled) 'Previously disabled task was enabled'
Write-Output 'PASS Install preserves already disabled startup task'
$before=$global:LiroTest_publishCalls
$global:LiroTest_unknownPath=$true
$global:LiroTest_running=$true
$rejected=$false
try { & $setup -Action Install } catch { $rejected=$true }
Assert-True ($rejected -and $global:LiroTest_publishCalls -eq $before -and $global:LiroTest_running) 'Unknown process was killed or publication proceeded'
Write-Output 'PASS unverified process path prevents publish and process kill'
$global:LiroTest_unknownPath=$false
$global:LiroTest_respawn=$true
$rejected=$false
try { & $setup -Action Install } catch { $rejected=$true }
Assert-True ($rejected -and $global:LiroTest_publishCalls -eq $before) 'Respawned process did not block publication'
Write-Output 'PASS respawned agent prevents publish'
$global:LiroTest_respawn=$false
$global:LiroTest_tasks['Liro.DevelopmentAgent'].Actions[0].Execute='C:\Other\agent.exe'
$global:LiroTest_events=@()
$rejected=$false
try { & $setup -Action Install } catch { $rejected=$true }
Assert-True ($rejected -and $global:LiroTest_events.Count -eq 0) 'Foreign task was modified'
Write-Output 'PASS foreign scheduled task is never modified'
$global:LiroTest_tasks['Liro.DevelopmentAgent'].Actions[0].Execute=Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent\app\Liro.DevelopmentAgent.exe'
$global:LiroTest_unknownPath=$true
$global:LiroTest_running=$true
$global:LiroTest_cimPath=Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent\app\Liro.DevelopmentAgent.exe'
$global:LiroTest_stopped=@()
& $setup -Action Install
Assert-True ($global:LiroTest_stopped.Count -eq 1 -and $global:LiroTest_stopped[0] -eq 101 -and -not $global:LiroTest_running) 'Fallback did not verify and stop exact installed agent'
Write-Output 'PASS null Process.Path uses verified fallback before stopping agent'
$global:LiroTest_running=$true
$global:LiroTest_stopped=@()
$global:LiroTest_cimPath='C:\Other\Liro.DevelopmentAgent.exe'
& $setup -Action StopAgent
Assert-True ($global:LiroTest_stopped.Count -eq 0 -and $global:LiroTest_running) 'Fallback killed foreign executable'
Write-Output 'PASS fallback path never authorizes stopping foreign executable'
$image=& (Join-Path $BackendRoot 'scripts/discord/Get-AgentProcessImage.ps1') -ProcessId $PID
Assert-True ($image -eq [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) 'Native image lookup returned wrong executable'
Write-Output 'PASS native image lookup resolves current process without module permissions'
Write-Output 'Todas las pruebas del instalador pasaron; operaciones de Windows simuladas.'



