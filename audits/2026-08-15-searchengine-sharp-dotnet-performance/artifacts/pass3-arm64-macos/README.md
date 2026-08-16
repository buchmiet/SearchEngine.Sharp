# Pass 3 benchmark artifacts (macOS ARM64)

Measured **2026-08-16** on `mac.home`. Source of truth: `*-report.csv` in this folder.

Full harness list: see [`pass3-x64-win/README.md`](../pass3-x64-win/README.md) (same BDN classes).

Environment: macOS ARM64, 8 cores, .NET 10.0.10. File corpus @ 100k, seed 2026.

## Selectivity pipeline — post-implementation

Source: [`post-implementation/SearchEngine.Sharp.MicroBenchmarks.SelectivityPipelineBenchmark-report.csv`](post-implementation/SearchEngine.Sharp.MicroBenchmarks.SelectivityPipelineBenchmark-report.csv)

Hit counts (`SelectivityPipelineCounts`): query `"report"` (Within), **textHitCount=10,000**, **postFacetHitCount=4**.

| Scenario | Mean | Median |
|----------|-----:|-------:|
| 0 text hits + NaturalSort cold | **40.7 μs** | **38.8 μs** |
| Within+Facet+NaturalSort cold | **233.5 μs** | **236.7 μs** |
| Within+Facet SnapshotOrder | **407 μs** | **196.3 μs** |

Cross-arch post-implementation (same harness):

| Scenario | x64-win | macOS ARM64 |
|----------|-------:|------------:|
| 0 hits + NaturalSort cold | **29.3 μs** | **40.7 μs** |
| Within+Facet+NaturalSort cold | **155.6 μs** | **233.5 μs** |
| Within+Facet SnapshotOrder (median) | **109 μs** | **196 μs** |

Selectivity-aware pipeline removes the pre-implementation **~24.9 ms** E2E NaturalSort path on x64; ARM64 E2E is sub-millisecond for the same sparse-final-result scenario.

## NaturalSort vs K (component, K=0)

From `NaturalSortSelectivityBenchmark-report.csv` — global full-N permutation at zero hits:

| Approach | Mean (K=0) |
|----------|----------:|
| Current — full N permutation + scan | **~127 ms** (high variance) |
| Sort K — precomputed keys | **~180 μs** |

Confirms selectivity-blind global permutation remains expensive when measured as a component (even though `Find` E2E is fast after pass 3).

## Other CSVs in this folder

- `BitSetMaterializationBenchmark-report.csv`
- `FacetSelectivityBenchmark-report.csv`
- `FileMaskGlobBenchmark-report.csv`
- `SnapshotBuildAllocationBenchmark-report.csv`
- `NaturalSortSelectivityBenchmark-report.csv`
- `NaturalSortCrossoverBenchmark-report.csv`
- `SelectivityPipelineBenchmark-report.csv` (pre-implementation label; same code path as post-impl on current main)
