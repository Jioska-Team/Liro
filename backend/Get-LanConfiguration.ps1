[CmdletBinding()]
param([int]$InterfaceIndex, [switch]$CheckFirewall)
# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/development/Get-LanConfiguration.ps1') @PSBoundParameters @args
