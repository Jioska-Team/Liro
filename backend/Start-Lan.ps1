[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [int]$InterfaceIndex,
    [Parameter(ParameterSetName = 'Run')][switch]$AgentMode,
    [Parameter(ParameterSetName = 'Configure')][switch]$ConfigureFirewall,
    [Parameter(ParameterSetName = 'Remove')][switch]$RemoveFirewall
)
# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/development/Start-Lan.ps1') @PSBoundParameters @args
