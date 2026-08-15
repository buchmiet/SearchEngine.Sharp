param(
    [string]$OutputRoot = "",
    [switch]$SkipHistorical
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$BenchRoot = Join-Path $RepoRoot "benchmarks\SearchEngine.CompetitorBenchmarks"
if ($OutputRoot -eq "") {
    $OutputRoot = Join-Path $BenchRoot "results\x64-win"
}

Push-Location $BenchRoot
try {
    Write-Host "=== Sharp current ==="
    dotnet run -c Release --project csharp -- --implementation sharp-current --output $OutputRoot

    if (-not $SkipHistorical) {
        & (Join-Path $PSScriptRoot "run-sharp-historical.ps1") -OutputRoot $OutputRoot
    }

    Write-Host "Results in $OutputRoot"
}
finally {
    Pop-Location
}
