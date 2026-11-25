# Library comparison

Runs the same file-name workloads in SearchEngine.Sharp and in three other in-memory search libraries, on one
shared corpus of 100,000 file names (`corpus/file-search-100k-seed2026.json`). It compares libraries, not
languages.

| Library | Version | Adapter | How `Within` is answered |
|---|---|---|---|
| SearchEngine.Sharp | this repository (`sharp-current`), or an older tag | `csharp/` | `WordMatchMethod.Within` |
| Tantivy | 0.22 | `tantivy/` | case-insensitive `RegexQuery` on the raw name |
| MiniSearch | 7.x | `minisearch/` | bigram tokens, AND search, then a substring check |
| Bleve | 2.4 | `bleve/` | regexp on a keyword-analyzed name field |

## Workloads

[`workloads.json`](workloads.json) defines them: 3 warm-up and 10 measured iterations, median in nanoseconds.

| Kind | SearchEngine.Sharp | Other libraries |
|---|---|---|
| `within` | `Within` | as in the table above |
| `exact` | exact token | string equality scan |
| `glob` | `FileMask` glob | a port of the library's glob matcher |
| `within_facet` | `Within` with a size range | the library's hits, then a size filter |
| natural sort | `NaturalSortKeyBuilder` | a port of the key builder |

Workloads ending in `_cold` rebuild the index in every iteration, so they include the build cost of each library.

Every adapter reports its hit count, and `scripts/validate-hits.ps1` checks them against these values:

| Workload | Hits |
|---|---:|
| `within_snapshot` | 10,000 |
| `within_facet_snapshot` | 4 |
| `within_facet_natural_cold` | 4 |
| `zero_hits_natural_cold` | 0 |
| `exact_first` | 7 |
| `glob_pdf` | 6,667 |

## Running

Prerequisites: the .NET 11 SDK, Rust (cargo), Node.js, and Go 1.22 or later for Bleve (skipped when `go` is
missing).

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

```bash
./scripts/run-all.sh                      # results go to results/<host>; set SE_BENCH_OUTPUT to change it
SKIP_HISTORICAL=1 ./scripts/run-all.sh    # only the current SearchEngine.Sharp
```

`run-sharp-historical.ps1` and `run-sharp-historical.sh` check out older releases into git worktrees under `.worktrees/`
and run the same workloads against them.
