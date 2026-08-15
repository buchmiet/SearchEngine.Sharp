# Library benchmark harness (0.5.6)

Compares **search libraries** on a shared in-memory file-name corpus (`FileSearchDataFactory` @ 100k, seed 2026).

This harness is **not** a cross-language runtime shootout. Hand-rolled reference algorithms in other languages were removed — they do not represent real libraries and are misleading.

## Measured libraries

| Library | Version | Adapter path | Notes |
|---------|---------|--------------|-------|
| **SearchEngine.Sharp** | `sharp-0.5.0-initial` @ `1bd312c`, `sharp-0.5.5`, `sharp-current` | `csharp/` | Historical via git worktree (x64) |
| **Tantivy** | 0.22 | `tantivy/` | In-memory index; Within via case-insensitive `RegexQuery` on raw field |
| **MiniSearch** | 7.x | `minisearch/` | In-memory; Within via bigram tokenization + AND search + `includes()` verify |
| **Bleve** | 2.4 | `bleve/` | In-memory (`NewMemOnly`); Within via regexp on keyword-analyzed `name` field |

Each adapter maps the same workloads and reports verified `hit_count` (see `scripts/validate-hits.ps1`).

### Query semantics (per workload)

| Workload kind | Sharp | Tantivy | MiniSearch | Bleve |
|---------------|-------|---------|------------|-------|
| `within` | Bigram Within | `(?i).*query.*` regex on raw name | Bigram AND + substring verify | Regexp on keyword field |
| `exact` | Exact token match | String equality scan | String equality scan | String equality scan |
| `glob` | FileMask glob | Ported `GlobMatcher` | Ported `GlobMatcher` | Ported `GlobMatcher` |
| `within_facet` | Within + size range | Regex hits + size filter | Search hits + size filter | Regexp hits + size filter |
| `natural` sort | `NaturalSortKeyBuilder` | Ported key builder | Ported key builder | Ported key builder |

Cold workloads (`*_cold`, `zero_hits_natural_cold`) rebuild the index each iteration. External libraries pay full index-build cost on cold paths; Sharp's selectivity pipeline avoids that floor.

## Workloads

See [`workloads.json`](workloads.json). Methodology: 3 warmup + 10 measured iterations; median ns.

Expected hit counts (all libraries must match `sharp-current`):

| Workload | Hits |
|----------|-----:|
| `within_snapshot` | 10000 |
| `within_facet_snapshot` | 4 |
| `within_facet_natural_cold` | 4 |
| `zero_hits_natural_cold` | 0 |
| `exact_first` | 7 |
| `glob_pdf` | 6667 |

## Run

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

Historical Sharp (requires git worktree):

```powershell
./scripts/run-sharp-historical.ps1
```

Remote (macOS / homelab):

```bash
./scripts/run-all.sh
# SE_BENCH_OUTPUT=~/se-comp-bench/.../results/<host> ./scripts/run-all.sh
```

Bleve requires Go 1.22+ (`go run .` in `bleve/`). macOS SSH host has no Go — run Bleve on homelab or x64 with Go installed.

Evidence CSV: `audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/competitor-benchmarks/`.
