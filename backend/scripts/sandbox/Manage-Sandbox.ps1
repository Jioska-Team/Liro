[CmdletBinding()]
param(
    [ValidateSet('Validate','Build','Start','Status','Logs','Stop')]
    [string]$Action = 'Validate',
    [Parameter(Mandatory=$true)][string]$EnvFile
)
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$file = (Resolve-Path -LiteralPath $EnvFile).Path
$values = @{}
foreach ($line in Get-Content -LiteralPath $file) {
    if ($line -match '^\s*(#|$)') { continue }
    if ($line -notmatch '^([A-Z_]+)=(.*)$') { throw 'Invalid sandbox env file format.' }
    if ($Matches[1] -notin @('SANDBOX_DB_PASSWORD','SANDBOX_ADMIN_API_KEY','SANDBOX_API_PORT')) { throw 'Unexpected sandbox setting.' }
    if ($values.ContainsKey($Matches[1])) { throw 'Duplicate sandbox setting.' }
    $values[$Matches[1]]=$Matches[2]
}
foreach ($key in @('SANDBOX_DB_PASSWORD','SANDBOX_ADMIN_API_KEY')) {
    if ($values[$key] -notmatch '^[a-fA-F0-9]{64}$') { throw "Provide an independent 64-character random hexadecimal value for $key." }
}
if ($values['SANDBOX_DB_PASSWORD'] -eq $values['SANDBOX_ADMIN_API_KEY']) { throw 'Use different secrets.' }
if ($values.ContainsKey('SANDBOX_API_PORT') -and $values['SANDBOX_API_PORT'] -ne '15205') { throw 'This sandbox uses dedicated loopback port 15205.' }
# Never allow shell overrides to replace the validated file values.
$previous = @{}
$keys = @('SANDBOX_DB_PASSWORD','SANDBOX_ADMIN_API_KEY','SANDBOX_API_PORT','COMPOSE_PROFILES')
try {
    foreach ($key in $keys) {
        $previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process')
        $value = if ($values.ContainsKey($key)) { $values[$key] } elseif ($key -eq 'SANDBOX_API_PORT') { '15205' } else { '' }
        [Environment]::SetEnvironmentVariable($key,$value,'Process')
    }
    $compose = @('compose','--env-file',$file,'-p','liro-sandbox','-f',(Join-Path $backendRoot 'deploy/sandbox/compose.yaml'))
    & docker @compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox configuration validation failed.' }
    switch ($Action) {
        'Validate' { Write-Output 'Sandbox configuration valid; no resources started.'; return }
        'Build' { & docker @compose build api migrate }
        'Start' { & docker @compose up -d --build --wait api }
        'Status' { & docker @compose ps -a }
        'Logs' { & docker @compose logs --tail 100 api migrate }
        'Stop' { & docker @compose stop }
    }
    if ($LASTEXITCODE -ne 0) { throw "Sandbox action failed: $Action" }
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
}
