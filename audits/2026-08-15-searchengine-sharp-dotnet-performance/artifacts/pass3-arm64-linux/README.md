# Pass 3 benchmark artifacts (Ubuntu ARM64 / homelab)

Measured **2026-08-16** on homelab. Source of truth: `*-report.csv` in this folder.

See [`pass3-arm64-macos/README.md`](../pass3-arm64-macos/README.md) for methodology; numbers below are homelab-specific.

Environment: Ubuntu 26.04 ARM64, 4 cores, .NET 10.0.10.

## Selectivity pipeline — post-implementation

Source: [`post-implementation/SearchEngine.Sharp.MicroBenchmarks.SelectivityPipelineBenchmark-report.csv`](post-implementation/SearchEngine.Sharp.MicroBenchmarks.SelectivityPipelineBenchmark-report.csv)

Hit counts: **textHitCount=10,000**, **postFacetHitCount=4**.

| Scenario | Mean | Median |
|----------|-----:|-------:|
| 0 text hits + NaturalSort cold | **51.0 μs** | **51.0 μs** |
| Within+Facet+NaturalSort cold | **274.7 μs** | **262.2 μs** |
| Within+Facet SnapshotOrder | **496 μs** | **221.4 μs** |

Cross-arch post-implementation:

| Scenario | x64-win | Ubuntu ARM64 |
|----------|-------:|-------------:|
| 0 hits + NaturalSort cold | **29.3 μs** | **51.0 μs** |
| Within+Facet+NaturalSort cold | **155.6 μs** | **274.7 μs** |
| Within+Facet SnapshotOrder (median) | **109 μs** | **221 μs** |

## Other CSVs

Same set as macOS pass 3 folder — see [`pass3-arm64-macos/README.md`](../pass3-arm64-macos/README.md).
