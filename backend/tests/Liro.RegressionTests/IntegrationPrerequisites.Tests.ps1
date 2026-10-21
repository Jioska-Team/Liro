#requires -Version 5.1
param([Parameter(Mandatory)][string]$TestDll)
$ErrorActionPreference = 'Stop'
$TestDll = (Resolve-Path -LiteralPath $TestDll).Path
function Assert-Rejected([string]$Name, [string]$Postgres, [string]$Redis, [string]$Expected, [string]$Mode = '--check-integration') {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Arguments and EnvironmentVariables also work on Windows PowerShell 5.1.
    # TestDll is a resolved file path; Mode is one of the fixed switches below.
    $info.Arguments = '"' + $TestDll + '"'
    if ($Mode) { $info.Arguments += ' ' + $Mode }
    $info.EnvironmentVariables['LIRO_TEST_POSTGRES'] = $Postgres
    $info.EnvironmentVariables['LIRO_TEST_REDIS'] = $Redis
    $p = [Diagnostics.Process]::Start($info)
    try {
        $output = $p.StandardOutput.ReadToEndAsync()
        $errorOutput = $p.StandardError.ReadToEndAsync()
        if (!$p.WaitForExit(20000)) { $p.Kill(); throw "$Name exceeded timeout" }
        $text = $output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()
        if ($p.ExitCode -ne 2 -or !$text.Contains($Expected) -or $text.Contains('passed') -or $text.Contains('secret-marker')) { throw "$Name did not fail safely" }
        Write-Output "PASS $Name"
    } finally { $p.Dispose() }
}
Assert-Rejected 'Integration_RejectsMissingConnections' '' '' 'FAIL Integration_RequiredConnections'
Assert-Rejected 'Integration_RejectsMissingValkey' $env:LIRO_TEST_POSTGRES '' 'FAIL Integration_RequiredConnections'
Assert-Rejected 'Integration_RejectsApplicationDatabase' 'Host=127.0.0.1;Database=lirodb;Password=secret-marker' $env:LIRO_TEST_REDIS 'FAIL Integration_PostgreSQLAvailable'
Assert-Rejected 'Integration_RejectsUnavailablePostgres' 'Host=127.0.0.1;Port=1;Database=liro_test_unavailable;Username=test;Password=secret-marker' $env:LIRO_TEST_REDIS 'FAIL Integration_PostgreSQLAvailable'
Assert-Rejected 'Integration_RejectsUnavailableValkey' $env:LIRO_TEST_POSTGRES '127.0.0.1:1,password=secret-marker,defaultDatabase=15' 'FAIL Integration_ValkeyAvailable'
Assert-Rejected 'Integration_RejectsDefaultValkeyDatabase' $env:LIRO_TEST_POSTGRES '127.0.0.1:1' 'FAIL Integration_ValkeyAvailable'

Assert-Rejected 'Integration_RejectsMissingPostgres' '' $env:LIRO_TEST_REDIS 'FAIL Integration_RequiredConnections'
Assert-Rejected 'Integration_RejectsWhitespaceConnections' '   ' '   ' 'FAIL Integration_RequiredConnections'
Assert-Rejected 'Integration_FullRunRequiresConnections' '' '' 'FAIL Integration_RequiredConnections' ''
Assert-Rejected 'Integration_SubsetRequiresConnections' '' '' 'FAIL Integration_RequiredConnections' '--catalog-reads-only'
