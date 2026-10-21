#requires -Version 5.1
param([string]$AspireTempRoot = $env:TEMP)
$ErrorActionPreference = 'Stop'
# Fail closed: never leave partial or stale credentials after failed discovery.
$env:LIRO_TEST_POSTGRES = $null
$env:LIRO_TEST_REDIS = $null
$runs = @(Get-ChildItem -LiteralPath $AspireTempRoot -Directory -Filter 'aspire-dcp*' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 5)
foreach ($run in $runs) {
    $kube = Join-Path $run.FullName 'kubeconfig'
    if (!(Test-Path -LiteralPath $kube)) { continue }
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        # Windows PowerShell turns native stderr into errors under Stop.
        # Suppress diagnostic text (it can contain connection details), check exit code.
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $json = & kubectl --kubeconfig $kube --request-timeout=8s get executables -o json 2>$null
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousPreference }
        if ($code -ne 0) { continue }
        try { $resources = ($json | ConvertFrom-Json -ErrorAction Stop).items }
        catch { continue }
        foreach ($resource in $resources) {
            if ($resource.status.state -ne 'Running') { continue }
            $pg = @($resource.spec.env | Where-Object name -eq 'ConnectionStrings__lirodb')
            $redis = @($resource.spec.env | Where-Object name -eq 'ConnectionStrings__valkey')
            if ($pg.Count -ne 1 -or $redis.Count -ne 1) { continue }
            if ([string]::IsNullOrWhiteSpace($pg[0].value) -or [string]::IsNullOrWhiteSpace($redis[0].value)) { continue }
            try {
                $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
                $builder.set_ConnectionString($pg[0].value)
                if (!$builder.ContainsKey('Host') -or [string]::IsNullOrWhiteSpace([string]$builder.get_Item('Host'))) { continue }
                $builder.set_Item('Database', 'liro_test_backup_validation')
                $testRedis = ($redis[0].value -replace '(?i),?\s*defaultDatabase\s*=\s*\d+', '') + ',defaultDatabase=15'
            } catch { continue }
            $env:LIRO_TEST_POSTGRES = $builder.get_ConnectionString()
            $env:LIRO_TEST_REDIS = $testRedis
            Write-Output 'Conexiones de pruebas cargadas: liro_test_backup_validation y Valkey DB 15. La suite comprobara su disponibilidad.'
            return
        }
    }
}
throw 'No se encontraron ambas conexiones en una instancia activa de Aspire. Las variables de pruebas quedaron vacias; no se modifico ningun servicio.'
