$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet format whitespace $PSScriptRoot --folder --verify-no-changes --exclude (Join-Path $PSScriptRoot 'src/Liro.Infrastructure/Persistence/Migrations') --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build Liro.slnx --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/Liro.AppHost.RegressionTests --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
