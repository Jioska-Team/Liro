[CmdletBinding()]
param([int]$InterfaceIndex, [switch]$CheckFirewall)
$ErrorActionPreference = 'Stop'
$interfaces = @(Get-NetIPConfiguration | Where-Object {
    $_.IPv4DefaultGateway -and $_.NetProfile.NetworkCategory -eq 'Private' -and
    (-not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex)
})
if ($interfaces.Count -ne 1) {
    throw 'Selecciona una conexion privada con puerta de enlace: Get-NetIPConfiguration. Ejecuta de nuevo con -InterfaceIndex NUMERO. No cambies a privada una red publica.'
}
$interface = $interfaces[0]
$addresses = @(Get-NetIPAddress -InterfaceIndex $interface.InterfaceIndex -AddressFamily IPv4 |
    Where-Object { $_.AddressState -eq 'Preferred' -and -not $_.SkipAsSource })
if ($addresses.Count -ne 1) { throw 'La interfaz debe tener una unica IPv4 preferida.' }
$lanAddress = $addresses[0].IPAddress
$prefix = [int]$addresses[0].PrefixLength
$bytes = [System.Net.IPAddress]::Parse($lanAddress).GetAddressBytes()
$privateRange = ($bytes[0] -eq 10) -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
$minimumPrefix = if ($bytes[0] -eq 10) { 8 } elseif ($bytes[0] -eq 172) { 12 } else { 16 }
if (-not $privateRange -or $prefix -lt $minimumPrefix -or $prefix -gt 30) {
    throw 'Se requiere una subred IPv4 privada valida.'
}
$networkBytes = for ($i = 0; $i -lt 4; $i++) {
    $bits = [Math]::Max(0, [Math]::Min(8, $prefix - 8 * $i))
    $byteMask = [int](256 - [Math]::Pow(2, 8 - $bits))
    $bytes[$i] -band $byteMask
}
$subnet = ($networkBytes -join '.') + '/' + $prefix
$maskBytes = for ($i = 0; $i -lt 4; $i++) {
    $bits = [Math]::Max(0, [Math]::Min(8, $prefix - 8 * $i))
    [int](256 - [Math]::Pow(2, 8 - $bits))
}
$dottedSubnet = ($networkBytes -join '.') + '/' + ($maskBytes -join '.')

if ($CheckFirewall) {
    $rule = Get-NetFirewallRule -Name 'Liro-Api-Lan-ReadOnly' -ErrorAction Stop
    $filter = $rule | Get-NetFirewallAddressFilter
    $ports = $rule | Get-NetFirewallPortFilter
    if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Allow' -or $rule.Direction -ne 'Inbound' -or
        $rule.Profile -ne 'Private' -or $filter.LocalAddress -notcontains $lanAddress -or
        ($filter.RemoteAddress -notcontains $subnet -and $filter.RemoteAddress -notcontains $dottedSubnet) -or $ports.LocalPort -notcontains '5205') {
        throw 'La regla LAN no coincide con la red actual. Configura el firewall localmente.'
    }
}
[pscustomobject]@{ Address=$lanAddress; PrefixLength=$prefix; InterfaceIndex=$interface.InterfaceIndex; InterfaceAlias=$interface.InterfaceAlias; Subnet=$subnet } | ConvertTo-Json -Compress

