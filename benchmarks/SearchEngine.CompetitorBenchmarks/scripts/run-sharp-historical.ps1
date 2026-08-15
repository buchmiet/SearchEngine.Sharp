param(
    [string]$OutputRoot = "",
    [string[]]$Refs = @("1bd312c", "v0.5.5")
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$BenchRoot = Join-Path $RepoRoot "benchmarks\SearchEngine.CompetitorBenchmarks"
$WorktreesRoot = Join-Path $BenchRoot ".worktrees"
if ($OutputRoot -eq "") {
    $OutputRoot = Join-Path $BenchRoot "results\x64-win"
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$refMeta = @{
    "1bd312c" = @{ Impl = "sharp-0.5.0-initial"; NoFacet = $true; NoGlob = $true }
    "v0.5.5"  = @{ Impl = "sharp-0.5.5"; NoFacet = $false; NoGlob = $false }
}

foreach ($ref in $Refs) {
    $sha = git -C $RepoRoot rev-parse $ref
    $safeName = ($ref -replace '[\\/:]', '-')
    $worktree = Join-Path $WorktreesRoot $safeName
    if (-not (Test-Path $worktree)) {
        New-Item -ItemType Directory -Force -Path $WorktreesRoot | Out-Null
        git -C $RepoRoot worktree add $worktree $sha --detach
    }

    $meta = $refMeta[$ref]
    if ($null -eq $meta) {
        $meta = @{ Impl = "sharp-$safeName"; NoFacet = $false; NoGlob = $false }
    }

    $args = @(
        "run", "-c", "Release",
        "--project", (Join-Path $BenchRoot "csharp"),
        "/p:SharpSourceRoot=$worktree",
        "--",
        "--mode", "sharp",
        "--implementation", $meta.Impl,
        "--output", $OutputRoot,
        "--git-sha", $sha
    )

    if ($meta.NoFacet) { $args += "--no-facet" }
    if ($meta.NoGlob) { $args += "--no-glob" }

    Write-Host "=== $($meta.Impl) @ $sha ==="
    dotnet @args
}

Write-Host "Historical Sharp results in $OutputRoot"
