#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/maintenance/Database.Common.ps1')
$oldConnection = $env:LIRO_BACKUP_POSTGRES
$root = Join-Path ([IO.Path]::GetTempPath()) ('liro-discovery-tests-' + [Guid]::NewGuid().ToString('N'))
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src/Liro.Api'))
$discoveryState = @{ Resources = @{}; Calls = 0 }
function kubectl {
    $discoveryState.Calls++
    $global:LASTEXITCODE = 0
    @{ items = @($discoveryState.Resources[$args[1]]) } | ConvertTo-Json -Depth 8 -Compress
}
function Resource([string]$Directory, [string]$Connection, [string]$State = 'Running') {
    @{ metadata=@{name='api-test'}; status=@{state=$State}; spec=@{
        workingDirectory=$Directory; env=@(@{name='ConnectionStrings__lirodb';value=$Connection})
    }}
}
function Assert-Equal($Actual, $Expected, [string]$Name) {
    if ($Actual -ne $Expected) { throw "$Name failed" }
    Write-Output "PASS $Name"
}
function Reject([string]$Name, [string]$Message) {
    try { $null = Get-LiroDatabaseConnection $root }
    catch {
        if ($_.Exception.Message -notlike "*$Message*" -or $_.Exception.Message -like '*fixture-connection*') { throw }
        Write-Output "PASS $Name"
        return
    }
    throw "$Name accepted unsafe discovery"
}
try {
    $null = New-Item -ItemType Directory -Path $root
    $kubes = foreach ($name in @('aspire-dcp-first','aspire-dcp-second')) {
        $dir = Join-Path $root $name
        $null = New-Item -ItemType Directory -Path $dir
        $kube = Join-Path $dir 'kubeconfig'
        [IO.File]::WriteAllText($kube, '')
        $kube
    }
    $env:LIRO_BACKUP_POSTGRES = $null
    foreach ($reverse in @($false,$true)) {
        $own = Resource $expected 'fixture-connection-own'
        $foreign = Resource (Join-Path $root 'other-checkout/src/Liro.Api') 'fixture-connection-foreign'
        $discoveryState.Resources[$kubes[0]] = @($(if ($reverse) {$own} else {$foreign}))
        $discoveryState.Resources[$kubes[1]] = @($(if ($reverse) {$foreign} else {$own}))
        Assert-Equal (Get-LiroDatabaseConnection $root) 'fixture-connection-own' "BackupDiscovery_SelectsCheckout_Order$reverse"
    }
    $discoveryState.Resources[$kubes[0]] = @(Resource $expected 'fixture-connection-one')
    $discoveryState.Resources[$kubes[1]] = @(Resource $expected 'fixture-connection-two')
    Reject 'BackupDiscovery_RejectsMultipleInstances' 'varias instancias'
    # Exercise the actual backup entry point: ambiguity must precede pg_dump and retention.
    $backupDirectory = Join-Path $root 'backups'
    $null = New-Item -ItemType Directory -Path $backupDirectory
    $previous = Join-Path $backupDirectory 'lirodb-previous.dump'
    [IO.File]::WriteAllText($previous, 'previous-backup-fixture')
    [IO.File]::WriteAllText(($previous + '.json'), '{}')
    $success = Join-Path $backupDirectory 'last-success.json'
    [IO.File]::WriteAllText($success, 'previous-success-fixture')
    $config = Join-Path $root 'config.json'
    @{RetainCopies=2;BackupDirectory=$backupDirectory;AspireTempRoot=$root;PgBin=(Join-Path $root 'no-pg-tools')} |
        ConvertTo-Json | Set-Content -LiteralPath $config
    $rejected = $false
    try { & (Join-Path $PSScriptRoot '../../scripts/maintenance/Backup-Database.ps1') -ConfigPath $config | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*varias instancias*') { throw }
        $rejected = $true
    }
    Assert-Equal $rejected $true 'BackupDiscovery_AmbiguousBackupStopsBeforePgTools'
    Assert-Equal ([IO.File]::ReadAllText($previous)) 'previous-backup-fixture' 'BackupDiscovery_AmbiguityPreservesArchive'
    Assert-Equal ([IO.File]::ReadAllText($success)) 'previous-success-fixture' 'BackupDiscovery_AmbiguityPreservesLastSuccess'
    $discoveryState.Resources[$kubes[1]] = @(Resource $expected 'fixture-connection-one')
    Reject 'BackupDiscovery_RejectsDuplicateInstancesEvenWithSameConnection' 'varias instancias'

    $discoveryState.Resources[$kubes[0]] = @(Resource $expected 'fixture-connection-stopped' 'Stopped')
    $discoveryState.Resources[$kubes[1]] = @(Resource '' 'fixture-connection-unidentified')
    Reject 'BackupDiscovery_RejectsStoppedAndUnidentified' 'No se encontro'
    $discoveryState.Resources[$kubes[0]] = @(Resource 'src/Liro.Api' 'fixture-connection-relative')
    Reject 'BackupDiscovery_RejectsRelativeIdentity' 'No se encontro'

    $discoveryState.Resources[$kubes[0]] = @(Resource ($expected.ToUpperInvariant() + '\') 'fixture-connection-own')
    $discoveryState.Resources[$kubes[1]] = @()
    Assert-Equal (Get-LiroDatabaseConnection $root) 'fixture-connection-own' 'BackupDiscovery_NormalizesWindowsPath'
    $discoveryState.Resources[$kubes[0]] = @(Resource $expected '')
    Reject 'BackupDiscovery_RejectsMissingConnection' 'conexion unica'

    $env:LIRO_BACKUP_POSTGRES = 'explicit-fixture-connection'
    $before = $discoveryState.Calls
    Assert-Equal (Get-LiroDatabaseConnection $root) 'explicit-fixture-connection' 'BackupDiscovery_ExplicitConnectionWins'
    Assert-Equal $discoveryState.Calls $before 'BackupDiscovery_ExplicitConnectionSkipsDiscovery'
} finally {
    $env:LIRO_BACKUP_POSTGRES = $oldConnection
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'liro-discovery-tests-*') { throw 'Unexpected cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
