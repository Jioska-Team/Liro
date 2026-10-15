param([string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)
$ErrorActionPreference = 'Stop'
# Mock all OS mutations. These tests must never touch real firewall rules, tasks or processes.
$global:LiroTest_tasks = @{}
$global:LiroTest_stopped = @()
$global:LiroTest_private = $true
$global:LiroTest_prefix = 24
$global:LiroTest_remoteSubnet = '192.168.50.0/24'
function Assert-True($condition, $message) { if (-not $condition) { throw $message } }
function Get-NetIPConfiguration { [pscustomobject]@{ IPv4DefaultGateway='gateway'; NetProfile=[pscustomobject]@{NetworkCategory=$(if ($global:LiroTest_private) {'Private'} else {'Public'})}; InterfaceIndex=7; InterfaceAlias='TestLAN' } }
function Get-NetIPAddress { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{AddressState='Preferred'; SkipAsSource=$false; IPAddress='192.168.50.10'; PrefixLength=$global:LiroTest_prefix} }
function Get-NetFirewallRule { [CmdletBinding()]param($Name) [pscustomobject]@{Enabled='True'; Action='Allow'; Direction='Inbound'; Profile='Private'} }
function Get-NetFirewallAddressFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { [pscustomobject]@{LocalAddress=@('192.168.50.10'); RemoteAddress=@($global:LiroTest_remoteSubnet)} } }
function Get-NetFirewallPortFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { [pscustomobject]@{LocalPort=@('5205')} } }
function Get-ScheduledTask { [CmdletBinding()]param($TaskName) if ($global:LiroTest_tasks.ContainsKey($TaskName)) { $global:LiroTest_tasks[$TaskName] } }
function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) [pscustomobject]@{Execute=$Execute; Argument=$Argument} }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn,$User) [pscustomobject]@{User=$User} }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) Assert-True ($RunLevel -eq 'Limited' -and $LogonType -eq 'Interactive') 'Elevated task requested'; [pscustomobject]@{User=$UserId} }
function New-ScheduledTaskSettingsSet { param($MultipleInstances,$ExecutionTimeLimit,$RestartCount,$RestartInterval,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries) Assert-True ($MultipleInstances -eq 'IgnoreNew') 'Duplicate task instances allowed'; [pscustomobject]@{} }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force) $global:LiroTest_tasks[$TaskName]=$Action }
function Unregister-ScheduledTask { param($TaskName,[switch]$Confirm) $global:LiroTest_tasks.Remove($TaskName) }
function Test-Path { param($LiteralPath,$PathType) $true }
function Get-Process { [CmdletBinding()]param($Name) @([pscustomobject]@{Id=101;Path=(Join-Path $env:LOCALAPPDATA 'Liro\DevelopmentAgent\app\Liro.DevelopmentAgent.exe')}, [pscustomobject]@{Id=102;Path='C:\Other\Liro.DevelopmentAgent.exe'}) }
function Stop-Process { param($Id) $global:LiroTest_stopped += $Id }

$helper = Join-Path $BackendRoot 'Get-LanConfiguration.ps1'
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

$setup = Join-Path $BackendRoot 'Setup-DevelopmentAgent.ps1'
& $setup -Action RegisterStartup
& $setup -Action RegisterStartup
Assert-True ($global:LiroTest_tasks.Count -eq 1 -and $global:LiroTest_tasks['Liro.DevelopmentAgent'].Argument -eq 'run') 'Repeated install duplicated startup task'
Write-Output 'PASS repeated startup registration is idempotent and unprivileged'
& $setup -Action Uninstall
Assert-True ($global:LiroTest_tasks.Count -eq 0 -and $global:LiroTest_stopped.Count -eq 1 -and $global:LiroTest_stopped[0] -eq 101) 'Uninstall touched a foreign process'
Write-Output 'PASS uninstall only stops exact installed agent and removes its task'
Write-Output '8/8 script tests passed (OS actions mocked)'


