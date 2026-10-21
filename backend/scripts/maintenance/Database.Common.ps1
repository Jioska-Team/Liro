Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LiroDatabaseConnection([string]$AspireTempRoot) {
    if ($env:LIRO_BACKUP_POSTGRES) { return $env:LIRO_BACKUP_POSTGRES }
    foreach ($run in (Get-ChildItem -LiteralPath $AspireTempRoot -Directory -Filter 'aspire-dcp*' | Sort-Object LastWriteTime -Descending | Select-Object -First 5)) {
        $kube = Join-Path $run.FullName 'kubeconfig'
        if (!(Test-Path -LiteralPath $kube)) { continue }
        $json = & kubectl --kubeconfig $kube --request-timeout=8s get executables -o json 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        $resources = ($json | ConvertFrom-Json).items
        foreach ($resource in $resources) {
            if ($resource.metadata.name -notlike 'api-*' -or $resource.status.state -ne 'Running') { continue }
            $connection = @($resource.spec.env | Where-Object name -eq 'ConnectionStrings__lirodb')
            if ($connection.Count -eq 1) { return $connection[0].value }
        }
    }
    throw 'No se encontro una instancia local de Aspire accesible. No se inicio ni reinicio ningun servicio.'
}

function Initialize-PgEnvironment([string]$Connection) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder.set_ConnectionString($Connection)
    $userKey = if ($builder.ContainsKey('Username')) { 'Username' } else { 'User ID' }
    $env:PGHOST = [string]$builder['Host']
    $env:PGPORT = if ($builder.ContainsKey('Port')) { [string]$builder['Port'] } else { '5432' }
    $env:PGUSER = [string]$builder[$userKey]
    $env:PGPASSWORD = [string]$builder['Password']
    $env:PGDATABASE = [string]$builder['Database']
    $env:PGCONNECT_TIMEOUT = '10'
    $env:PGAPPNAME = 'Liro.DatabaseMaintenance'
    $env:PGOPTIONS = '-c lock_timeout=5000 -c statement_timeout=1800000'
    if ($env:PGDATABASE -ne 'lirodb') { throw 'Se esperaba la base local lirodb. Revise la conexion antes de continuar.' }
}

function Invoke-Pg([string]$Bin, [string]$Tool, [string[]]$Arguments) {
    $exe = Join-Path $Bin ($Tool + '.exe')
    if (!(Test-Path -LiteralPath $exe)) { throw "Falta $Tool en $Bin" }
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(1800000)) { $process.Kill($true); throw "$Tool excedio 30 minutos." }
        $null = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "$Tool fallo (codigo $($process.ExitCode)); no se confirma el respaldo/restauracion." }
        return $output.GetAwaiter().GetResult().Trim()
    } finally { $process.Dispose() }
}

function Invoke-PgSql([string]$Bin, [string]$Sql) {
    Invoke-Pg $Bin 'psql' @('-X', '--no-password', '--tuples-only', '--no-align', '--set=ON_ERROR_STOP=1', '--command', $Sql)
}

function Save-PgEnvironment {
    $saved = @{}
    foreach ($name in @('PGHOST','PGPORT','PGUSER','PGPASSWORD','PGDATABASE','PGCONNECT_TIMEOUT','PGAPPNAME','PGOPTIONS')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    return $saved
}

function Restore-PgEnvironment($Saved) {
    foreach ($name in $Saved.Keys) { [Environment]::SetEnvironmentVariable($name, $Saved[$name], 'Process') }
}
