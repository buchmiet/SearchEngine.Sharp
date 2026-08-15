# Library benchmark artifacts

Measured 2026-08-15. Shared corpus: `FileSearchDataFactory` @ 100k, seed 2026. Harness: [`benchmarks/SearchEngine.CompetitorBenchmarks/`](../../../benchmarks/SearchEngine.CompetitorBenchmarks/README.md).

**Scope:** SearchEngine.Sharp (version history on x64) vs real search libraries — **Tantivy 0.22**, **MiniSearch 7**, **Bleve 2.4**. Hand-rolled cross-language scan baselines were removed.

Methodology: 3 warmup + 10 measured iterations; median latency in nanoseconds. All libraries verified identical `hit_count` per workload (`validate-hits.ps1`). Cold workloads rebuild the index each iteration.

Historical Sharp versions (x64 only, git worktree): `sharp-0.5.0-initial` @ `1bd312c`, `sharp-0.5.5`, `sharp-current`.

## Library comparison (x64-win, median µs)

| Workload | sharp-current | tantivy | minisearch |
|----------|--------------:|--------:|-----------:|
| `within_snapshot` | **35.1** | 8416.3 | 27310.7 |
| `within_facet_snapshot` | **61.7** | 9077.0 | 30354.2 |
| `within_facet_natural_cold` | **113.1** | 74072.6 | 34655.8 |
| `zero_hits_natural_cold` | **17.1** | 67841.9 | 12669.0 |
| `exact_first` | **38.5** | 128.9 | 728.4 |
| `glob_pdf` | **450.5** | 2791.3 | 14907.4 |

Sharp wins decisively on indexed Within + facet + cold NaturalSort. Tantivy/MiniSearch pay full index rebuild on cold workloads; Tantivy regex Within on 100k docs is also expensive on hot paths.

## SearchEngine.Sharp version comparison (x64-win)

| Workload | sharp-0.5.0-initial | sharp-0.5.5 | sharp-current |
|----------|--------------------:|------------:|--------------:|
| `within_snapshot` | 66.9 | 67.7 | **35.1** |
| `within_facet_snapshot` | — | 147.1 | **61.7** |
| `within_facet_natural_cold` | — | 28,545.5 | **113.1** |
| `zero_hits_natural_cold` | 31,760.4 | 27,013.3 | **17.1** |
| `exact_first` | 85.9 | 79.7 | **38.5** |
| `glob_pdf` | — | 416.8 | **450.5** |

Selectivity pipeline on main removes the ~25–31 ms cold NaturalSort floor on zero-hit and sparse facet queries.

## sharp-current vs libraries on ARM64

| Workload | arm64-macos Sharp | Tantivy | MiniSearch | arm64-linux Sharp | Tantivy | MiniSearch | Bleve |
|----------|------------------:|--------:|-----------:|------------------:|--------:|-----------:|------:|
| `within_snapshot` | **74.3** | 9905.1 | 44291.0 | **68.4** | 9154.7 | 53151.2 | 42881.1 |
| `within_facet_natural_cold` | **162.4** | 75202.5 | 40910.3 | **153.9** | 181640.0 | 44060.2 | 45075.7 |
| `zero_hits_natural_cold` | **23.0** | 70515.5 | 13540.1 | **27.1** | 135614.3 | 16107.9 | 14980.1 |

Bleve measured on arm64-linux only (homelab has Go; macOS SSH host does not).

## Layout

| Folder | Host | Contents |
|--------|------|----------|
| `x64-win/` | Cray Windows | Sharp 0.5.0-initial, 0.5.5, current; Tantivy; MiniSearch |
| `arm64-macos/` | mac.home | Sharp current; Tantivy; MiniSearch |
| `arm64-linux/` | homelab | Sharp current; Tantivy; MiniSearch; Bleve |

Each CSV: `{implementation}-library-benchmark.csv` + `{implementation}-environment.json` (Sharp only).

## Reproduce

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

```bash
# homelab / macOS
./scripts/run-all.sh
```
