param([Parameter(Mandatory = $true)][int]$ProcessId)
# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/discord/Get-AgentProcessImage.ps1') @PSBoundParameters @args
