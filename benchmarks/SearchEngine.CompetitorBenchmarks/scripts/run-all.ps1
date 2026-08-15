param(
    [string]$OutputRoot = "",
    [switch]$SkipHistorical,
    [switch]$SkipCompetitors
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$BenchRoot = Join-Path $RepoRoot "benchmarks\SearchEngine.CompetitorBenchmarks"
if ($OutputRoot -eq "") {
    $OutputRoot = Join-Path $BenchRoot "results\x64-win"
}

Push-Location $BenchRoot
try {
    Write-Host "=== Export corpus (if missing) + Sharp current ==="
    dotnet run -c Release --project csharp -- --mode sharp --implementation sharp-current --output $OutputRoot

    if (-not $SkipHistorical) {
        & (Join-Path $PSScriptRoot "run-sharp-historical.ps1") -OutputRoot $OutputRoot
    }

    if (-not $SkipCompetitors) {
        Write-Host "=== Rust scan baseline ==="
        Push-Location rust
        $env:SE_BENCH_OUTPUT = $OutputRoot
        $env:SE_BENCH_IMPL = "rust-scan"
        cargo run --release
        Pop-Location

        Write-Host "=== Node scan baseline ==="
        Push-Location node
        $env:SE_BENCH_OUTPUT = $OutputRoot
        $env:SE_BENCH_IMPL = "node-scan"
        node bench.mjs
        Pop-Location

        if (Get-Command go -ErrorAction SilentlyContinue) {
            Write-Host "=== Go scan baseline ==="
            Push-Location go
            $env:SE_BENCH_OUTPUT = $OutputRoot
            go run .
            Pop-Location
        }
    }

    Write-Host "Results in $OutputRoot"
}
finally {
    Pop-Location
}
