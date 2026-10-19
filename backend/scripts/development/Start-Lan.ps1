[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [int]$InterfaceIndex,
    [Parameter(ParameterSetName = 'Run')][switch]$AgentMode,
    [Parameter(ParameterSetName = 'Configure')][switch]$ConfigureFirewall,
    [Parameter(ParameterSetName = 'Remove')][switch]$RemoveFirewall
)

$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$ruleName = 'Liro-Api-Lan-ReadOnly'

if ($RemoveFirewall) {
    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host 'Regla LAN eliminada.'
    return
}

$configuration = & (Join-Path $PSScriptRoot 'Get-LanConfiguration.ps1') -InterfaceIndex $InterfaceIndex | ConvertFrom-Json
$lanAddress = $configuration.Address
$prefix = $configuration.PrefixLength
$subnet = $configuration.Subnet
$interface = $configuration
if ($ConfigureFirewall) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Abre PowerShell como administrador solo para configurar el firewall.'
    }
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
        throw 'La regla ya existe. Si cambio la red, usa -RemoveFirewall y vuelve a configurarla.'
    }
    New-NetFirewallRule -Name $ruleName -DisplayName 'Liro API LAN - solo consultas' `
        -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol TCP `
        -LocalAddress $lanAddress -LocalPort 5205 -RemoteAddress $subnet `
        -InterfaceAlias $interface.InterfaceAlias -EdgeTraversalPolicy Block | Out-Null
    Write-Host 'Firewall configurado para esta interfaz y subred privada. Inicia el servidor desde una consola normal.'
    return
}

$settings = @{
    'Lan__Enabled' = 'true'
    'Lan__Address' = $lanAddress
    'Lan__PrefixLength' = [string]$prefix
    'Lan__AllowedOrigins__0' = 'http://localhost:4200'
}
$previous = @{}
foreach ($key in $settings.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
Push-Location $backendRoot
try {
    foreach ($key in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    Write-Host "API para el frontend: http://${lanAddress}:5205/api/v1/items"
    Write-Host 'Solo consultas desde la subred local. No enviar credenciales por HTTP. CTRL+C para detener.'
    $project = Join-Path $backendRoot 'src/Liro.AppHost/Liro.AppHost.csproj'
    if ($AgentMode) {
        dotnet run --project $project --launch-profile https *> $null
    } else {
        dotnet run --project $project --launch-profile https
    }
    if ($LASTEXITCODE -ne 0) { throw "AppHost termino con codigo $LASTEXITCODE." }
}
finally {
    foreach ($key in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
    Pop-Location
}

