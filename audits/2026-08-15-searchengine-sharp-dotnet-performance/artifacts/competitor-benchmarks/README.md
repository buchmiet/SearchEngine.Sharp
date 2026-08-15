# Competitor benchmark artifacts (0.5.6)

Measured 2026-08-15. Shared corpus: `FileSearchDataFactory` @ 100k, seed 2026 (committed JSON). Harness: [`benchmarks/SearchEngine.CompetitorBenchmarks/`](../../../benchmarks/SearchEngine.CompetitorBenchmarks/README.md).

Methodology: 3 warmup + 10 measured iterations; median latency in nanoseconds. All implementations verified identical `hit_count` per workload. Sharp **cold** workloads rebuild the index each iteration; cross-language baselines use in-memory linear scan (`notes=hot-corpus`).

Historical Sharp versions (x64 only, git worktree): `sharp-0.5.0-initial` @ `1bd312c`, `sharp-0.5.5` @ `21dd346`, `sharp-current` @ main.

## Key workload: cold NaturalSort paths

| Implementation | Platform | `zero_hits_natural_cold` | `within_facet_natural_cold` |
|----------------|----------|-------------------------:|----------------------------:|
| **sharp-0.5.0-initial** | x64-win | 25,298 µs | — (no facet) |
| **sharp-0.5.5** | x64-win | 24,945 µs | 25,988 µs |
| **sharp-current** | x64-win | **20.9 µs** | **112.1 µs** |
| **sharp-current** | arm64-macos | **23.4 µs** | **146.2 µs** |
| **sharp-current** | arm64-linux | **42.3 µs** | **163.0 µs** |
| rust-scan | x64-win | 7,254 µs | 8,495 µs |
| go-scan | arm64-macos | 3,095 µs | 3,835 µs |
| node-scan | x64-win | 1,672 µs | 4,859 µs |

Selectivity pipeline (`9c29c69`) removes the ~25 ms cold NaturalSort floor on zero-hit and sparse facet queries. Cross-language scans remain **O(N)** per query (~2–8 ms @ 100k) — Sharp indexed Within+Facet is **~60–110 µs** hot, **~110–170 µs** cold on ARM64/x64.

## Indexed Within @ 100k (`within_snapshot`, hot)

| Implementation | x64-win | arm64-macos | arm64-linux |
|----------------|--------:|------------:|------------:|
| sharp-current | 63.8 µs | 68.3 µs | 66.1 µs |
| rust-scan | 7,128 µs | 7,919 µs | 5,925 µs |
| go-scan | — | 3,247 µs | 3,655 µs |
| node-scan | 2,157 µs | 3,435 µs | 4,335 µs |

## Layout

| Folder | Host | RID |
|--------|------|-----|
| `x64-win/` | Cray Windows | win-x64 |
| `arm64-macos/` | mac.home | osx-arm64 |
| `arm64-linux/` | homelab Ubuntu | linux-arm64 |

Each folder contains `*-competitor-benchmark.csv` and `*-environment.json` per implementation.

## Reproduce

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

Historical Sharp requires a git checkout (worktrees). ARM64/Linux/macOS: `SKIP_HISTORICAL=1 bash scripts/run-all.sh` when deployed without `.git`.
