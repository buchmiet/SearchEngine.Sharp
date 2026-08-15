# Library benchmark artifacts (SearchEngine.Sharp versions)

Measured 2026-08-15. Shared corpus: `FileSearchDataFactory` @ 100k, seed 2026. Harness: [`benchmarks/SearchEngine.CompetitorBenchmarks/`](../../../benchmarks/SearchEngine.CompetitorBenchmarks/README.md).

**Scope:** SearchEngine.Sharp only (first public release vs 0.5.5 vs current main). External libraries (Tantivy, MiniSearch, Bleve, …) are planned — not included yet.

Methodology: 3 warmup + 10 measured iterations; median latency in nanoseconds. Verified identical `hit_count` per workload. Cold workloads rebuild the index each iteration.

## SearchEngine.Sharp version comparison (x64-win)

| Workload | sharp-0.5.0-initial | sharp-0.5.5 | sharp-current |
|----------|--------------------:|------------:|--------------:|
| `within_snapshot` | 78.6 µs | 65.4 µs | **63.8 µs** |
| `within_facet_snapshot` | — | 172.1 µs | **61.5 µs** |
| `within_facet_natural_cold` | — | 25,988 µs | **112.1 µs** |
| `zero_hits_natural_cold` | 25,298 µs | 24,945 µs | **20.9 µs** |
| `exact_first` | 78.8 µs | 78.5 µs | **37.2 µs** |
| `glob_pdf` | — | 399.3 µs | **653.0 µs** |

Selectivity pipeline on main (`9c29c69`) removes the ~25 ms cold NaturalSort floor on zero-hit and sparse facet queries.

## sharp-current on ARM64

| Workload | arm64-macos | arm64-linux |
|----------|------------:|------------:|
| `within_snapshot` | 68.3 µs | 66.1 µs |
| `within_facet_natural_cold` | 146.2 µs | 163.0 µs |
| `zero_hits_natural_cold` | 23.4 µs | 42.3 µs |

## Layout

| Folder | Host | Contents |
|--------|------|----------|
| `x64-win/` | Cray Windows | Sharp 0.5.0-initial, 0.5.5, current |
| `arm64-macos/` | mac.home | Sharp current |
| `arm64-linux/` | homelab | Sharp current |

Each CSV: `{implementation}-library-benchmark.csv` + `{implementation}-environment.json`.

## Reproduce

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```
