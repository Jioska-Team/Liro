[CmdletBinding()]
param(
    [ValidateSet('Install','Configure','ConfigureWebshare','ConfigureOwner','InstallPanel','StopAgent','Run','RegisterStartup','UnregisterStartup','Uninstall')]
    [string]$Action = 'Install'
)
# Legacy compatibility entry point.
& (Join-Path $PSScriptRoot 'scripts/discord/Setup-DiscordLocalControl.ps1') @PSBoundParameters @args
