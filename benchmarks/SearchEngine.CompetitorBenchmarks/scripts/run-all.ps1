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
    Write-Host "=== SearchEngine.Sharp current ==="
    dotnet run -c Release --project csharp -- --implementation sharp-current --output $OutputRoot

    if (-not $SkipHistorical) {
        & (Join-Path $PSScriptRoot "run-sharp-historical.ps1") -OutputRoot $OutputRoot
    }

    Write-Host "=== Tantivy ==="
    Push-Location tantivy
    $env:SE_BENCH_OUTPUT = $OutputRoot
    $env:SE_BENCH_IMPL = "tantivy"
    cargo run --release
    Pop-Location

    Write-Host "=== MiniSearch ==="
    Push-Location minisearch
    if (-not (Test-Path node_modules)) { npm install --silent }
    $env:SE_BENCH_OUTPUT = $OutputRoot
    $env:SE_BENCH_IMPL = "minisearch"
    node bench.mjs
    Pop-Location

    if (Get-Command go -ErrorAction SilentlyContinue) {
        Write-Host "=== Bleve ==="
        Push-Location bleve
        $env:SE_BENCH_OUTPUT = $OutputRoot
        $env:SE_BENCH_IMPL = "bleve"
        go run .
        Pop-Location
    }

    & (Join-Path $PSScriptRoot "validate-hits.ps1") -ResultsDir $OutputRoot
    Write-Host "Results in $OutputRoot"
}
finally {
    Pop-Location
}
