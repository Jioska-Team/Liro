#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigPath)
. (Join-Path $PSScriptRoot 'Database.Common.ps1')
$saved = Save-PgEnvironment
$lock = $null
$partial = $null
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($config.RetainCopies -lt 2) { throw 'Conserve al menos dos respaldos.' }
    $folder = [IO.Path]::GetFullPath($config.BackupDirectory)
    if (!(Test-Path -LiteralPath $folder)) { throw 'La carpeta de respaldos debe existir.' }
    $lock = [IO.File]::Open((Join-Path $folder 'maintenance.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Initialize-PgEnvironment (Get-LiroDatabaseConnection $config.AspireTempRoot)
    $server = Invoke-PgSql $config.PgBin "SELECT current_setting('server_version')"
    $name = 'lirodb-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $partial = Join-Path $folder ($name + '.partial')
    $target = Join-Path $folder ($name + '.dump')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-Pg $config.PgBin 'pg_dump' @('--no-password','--format=custom','--compress=1','--lock-wait-timeout=5s','--file',$partial)
    $null = Invoke-Pg $config.PgBin 'pg_restore' @('--list',$partial)
    Move-Item -LiteralPath $partial -Destination $target
    $manifest = [ordered]@{ File=[IO.Path]::GetFileName($target); Database='lirodb'; CreatedAtUtc=[DateTime]::UtcNow.ToString('o'); ServerVersion=$server; Bytes=(Get-Item -LiteralPath $target).Length; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2) }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath ($target + '.json') -Encoding utf8
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'last-success.json') -Encoding utf8
    # Only completed backups from this script are eligible, and only after a new success.
    $completed = @(Get-ChildItem -LiteralPath $folder -File -Filter 'lirodb-*.dump' | Where-Object { Test-Path -LiteralPath ($_.FullName + '.json') } | Sort-Object Name -Descending)
    foreach ($old in ($completed | Select-Object -Skip $config.RetainCopies)) {
        Remove-Item -LiteralPath $old.FullName
        Remove-Item -LiteralPath ($old.FullName + '.json')
    }
    $manifest | ConvertTo-Json -Compress
} catch {
    if ($lock -and $folder) {
        @{ AtUtc=[DateTime]::UtcNow.ToString('o'); Error=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'last-failure.json')
    }
    throw
} finally {
    if ($partial -and (Test-Path -LiteralPath $partial)) { Remove-Item -LiteralPath $partial }
    if ($lock) { $lock.Dispose() }
    Restore-PgEnvironment $saved
}
