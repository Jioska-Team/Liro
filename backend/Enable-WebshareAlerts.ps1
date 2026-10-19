# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/discord/Enable-WebshareAlerts.ps1') @PSBoundParameters @args
