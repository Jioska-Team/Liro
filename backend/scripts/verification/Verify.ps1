$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $backendRoot
try {
    dotnet format whitespace $backendRoot --folder --verify-no-changes --exclude (Join-Path $backendRoot 'src/Liro.Infrastructure/Persistence/Migrations') --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build Liro.slnx --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.AppHost.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.Discord.LocalControl.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.Discord.Alerts.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $backendRoot 'tests/Liro.Discord.LocalControl.RegressionTests/Scripts.Tests.ps1')
}
finally {
    Pop-Location
}
