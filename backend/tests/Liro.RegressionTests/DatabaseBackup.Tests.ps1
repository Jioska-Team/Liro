#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$BackupFile,
    [Parameter(Mandatory)][ValidatePattern('^liro_restore_[a-z0-9_]{1,40}$')][string]$RestoredDatabase
)
$ErrorActionPreference='Stop'
$scripts=Join-Path $PSScriptRoot '../../scripts/maintenance'
. (Join-Path $scripts 'Database.Common.ps1')
$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$saved=Save-PgEnvironment
$lock=$null
$tampered=Join-Path $config.BackupDirectory ('test-corrupt-' + [Guid]::NewGuid().ToString('N') + '.dump')
function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Expected) {
    $rejected=$false
    try { & $Action | Out-Null } catch { if($_.ToString() -notlike "*$Expected*"){throw}; $rejected=$true }
    if(!$rejected){throw "$Name accepted an unsafe operation"}
    Write-Output "PASS $Name"
}
try {
    Initialize-PgEnvironment (Get-LiroDatabaseConnection $config.AspireTempRoot)
    $env:PGDATABASE=$RestoredDatabase
    $before=Invoke-PgSql $config.PgBin 'SELECT count(*) FROM "MarketHistoryPoints"'
    if([long]$before -le 0){throw 'The restored fixture has no history'}
    Assert-Rejected 'Backup_RejectsApplicationRestoreTarget' { & (Join-Path $scripts 'Restore-DatabaseBackup.ps1') -ConfigPath $ConfigPath -BackupFile $BackupFile -DatabaseName 'lirodb' } 'DatabaseName'
    Assert-Rejected 'Backup_DoesNotOverwriteExistingDatabase' { & (Join-Path $scripts 'Restore-DatabaseBackup.ps1') -ConfigPath $ConfigPath -BackupFile $BackupFile -DatabaseName $RestoredDatabase } 'psql fallo'
    $after=Invoke-PgSql $config.PgBin 'SELECT count(*) FROM "MarketHistoryPoints"'
    if($before -ne $after){throw 'Existing history was changed'}
    Write-Output 'PASS Backup_PreservesExistingRestoredHistory'
    'corrupt fixture' | Set-Content -LiteralPath $tampered
    @{Sha256=('0'*64)} | ConvertTo-Json | Set-Content -LiteralPath ($tampered+'.json')
    Assert-Rejected 'Backup_RejectsCorruptArchiveBeforeConnecting' { & (Join-Path $scripts 'Restore-DatabaseBackup.ps1') -ConfigPath $ConfigPath -BackupFile $tampered } 'hash'
    $success=Get-FileHash -LiteralPath (Join-Path $config.BackupDirectory 'last-success.json')
    $lock=[IO.File]::Open((Join-Path $config.BackupDirectory 'maintenance.lock'),'OpenOrCreate','ReadWrite','None')
    Assert-Rejected 'Backup_RejectsConcurrentExecution' { & (Join-Path $scripts 'Backup-Database.ps1') -ConfigPath $ConfigPath } 'Open'
    $lock.Dispose();$lock=$null
    if((Get-FileHash -LiteralPath (Join-Path $config.BackupDirectory 'last-success.json')).Hash -ne $success.Hash){throw 'Failed operation replaced successful backup status'}
    Write-Output 'PASS Backup_FailurePreservesLastSuccess'
} finally {
    if($lock){$lock.Dispose()}
    foreach($file in @($tampered,($tampered+'.json'))){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}}
    Restore-PgEnvironment $saved
}
