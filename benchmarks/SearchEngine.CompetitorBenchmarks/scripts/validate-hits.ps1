param(
    [string]$ResultsDir = ""
)

$ErrorActionPreference = "Stop"
$BenchRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ResultsDir -eq "") {
    $ResultsDir = Join-Path $BenchRoot "results\x64-win"
}

$referencePath = Join-Path $ResultsDir "sharp-current-library-benchmark.csv"
if (-not (Test-Path $referencePath)) {
    throw "Reference not found: $referencePath (run Sharp benchmark first)"
}

$expected = @{}
Import-Csv $referencePath | ForEach-Object { $expected[$_.workload] = [int]$_.hit_count }

$failed = $false
Get-ChildItem $ResultsDir -Filter "*-library-benchmark.csv" | ForEach-Object {
    $impl = $_.BaseName -replace '-library-benchmark$',''
    if ($impl -eq "sharp-0.5.0-initial" -or $impl -eq "sharp-0.5.5") { return }
    foreach ($row in Import-Csv $_.FullName) {
        if (-not $expected.ContainsKey($row.workload)) { continue }
        $exp = $expected[$row.workload]
        $got = [int]$row.hit_count
        if ($got -ne $exp) {
            Write-Host "FAIL $impl $($row.workload): expected $exp got $got" -ForegroundColor Red
            $failed = $true
        }
    }
}

if ($failed) { exit 1 }
Write-Host "All library benchmarks match Sharp hit counts."
