#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$BackupFile,
    [ValidatePattern('^liro_restore_[a-z0-9_]{1,40}$')][string]$DatabaseName = ('liro_restore_' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
)
. (Join-Path $PSScriptRoot 'Database.Common.ps1')
$saved = Save-PgEnvironment
$lock = $null
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    $lock = [IO.File]::Open((Join-Path $config.BackupDirectory 'maintenance.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $manifest = Get-Content -LiteralPath ($BackupFile + '.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $BackupFile -Algorithm SHA256).Hash -ne $manifest.Sha256) { throw 'El hash del respaldo no coincide. No se creo ninguna base.' }
    Initialize-PgEnvironment (Get-LiroDatabaseConnection $config.AspireTempRoot)
    # CREATE fails if it exists. Never DROP, CLEAN, overwrite, or run application migrations.
    $null = Invoke-PgSql $config.PgBin ('CREATE DATABASE "' + $DatabaseName + '" TEMPLATE template0')
    $env:PGDATABASE = $DatabaseName
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-Pg $config.PgBin 'pg_restore' @('--no-password','--exit-on-error','--single-transaction','--no-owner','--no-privileges','--dbname',$DatabaseName,$BackupFile)
    $counts = Invoke-PgSql $config.PgBin @'
SELECT json_build_object('Items',(SELECT count(*) FROM "Items"),
 'MarketWatches',(SELECT count(*) FROM "MarketWatches"),
 'MarketHistoryPoints',(SELECT count(*) FROM "MarketHistoryPoints"),
 'MarketRapSnapshots',(SELECT count(*) FROM "MarketRapSnapshots"));
'@
    $result = @{ Database=$DatabaseName; Backup=[IO.Path]::GetFileName($BackupFile); AtUtc=[DateTime]::UtcNow.ToString('o'); Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2); Counts=($counts | ConvertFrom-Json) }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $config.BackupDirectory ($DatabaseName + '.verified.json'))
    $result | ConvertTo-Json -Depth 4
} finally {
    if ($lock) { $lock.Dispose() }
    Restore-PgEnvironment $saved
}
