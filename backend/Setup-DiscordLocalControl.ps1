[CmdletBinding()]
param(
    [ValidateSet('Install','Configure','ConfigureWebshare','ConfigureOwner','InstallPanel','StopAgent','Run','RegisterStartup','UnregisterStartup','Uninstall')]
    [string]$Action = 'Install'
)
# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/discord/Setup-DiscordLocalControl.ps1') @PSBoundParameters @args
