#requires -Version 5.1
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:LIRO_TEST_POSTGRES) -or [string]::IsNullOrWhiteSpace($env:LIRO_TEST_REDIS)) {
    throw 'La validacion completa requiere LIRO_TEST_POSTGRES (base liro_test_ separada) y LIRO_TEST_REDIS (base logica distinta de 0). No se permite omitir integracion.'
}
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
# Never build into the bin/obj directories of the running backend.
# Keep the snapshot for diagnosis; every invocation has its own directory.
$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('liro-verification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verificationRoot -ErrorAction Stop | Out-Null
& robocopy $backendRoot $verificationRoot /E /XJ /R:0 /W:0 /XD bin obj .git .vs artifacts TestResults /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'No se pudo crear la copia aislada de verificacion. No se inicio la compilacion.' }
$backendRoot = $verificationRoot
Write-Output "Verificacion aislada: $verificationRoot"
Push-Location $backendRoot
try {
    dotnet format whitespace $backendRoot --folder --verify-no-changes --exclude (Join-Path $backendRoot 'src/Liro.Infrastructure/Persistence/Migrations') --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build Liro.slnx --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $backendRoot 'tests/Liro.RegressionTests/IntegrationPrerequisites.Tests.ps1') -TestDll (Join-Path $backendRoot 'tests/Liro.RegressionTests/bin/Debug/net10.0/Liro.RegressionTests.dll')
    dotnet run --project tests/Liro.AppHost.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.Discord.LocalControl.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.Discord.Alerts.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $backendRoot 'tests/Liro.Discord.LocalControl.RegressionTests/Scripts.Tests.ps1')
    & pwsh -NoProfile -File (Join-Path $backendRoot 'tests/Liro.RegressionTests/DatabaseDiscovery.Tests.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
