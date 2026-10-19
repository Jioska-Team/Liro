# Compatibility entry point. Implementation lives under scripts/.
& (Join-Path $PSScriptRoot 'scripts/verification/Verify.ps1') @PSBoundParameters @args
