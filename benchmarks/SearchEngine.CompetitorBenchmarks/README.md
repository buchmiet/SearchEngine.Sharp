# Competitor benchmark suite (0.5.6)

Cross-language file-search workloads on a **shared committed corpus** (`FileSearchDataFactory` @ 100k, seed 2026).

## Workloads

Defined in [`workloads.json`](workloads.json). All implementations must report identical `hit_count` for correctness.

| ID | Sharp semantics | Competitor semantics |
|----|-----------------|----------------------|
| `within_snapshot` | Within + SnapshotOrder | Linear scan, case-insensitive substring |
| `within_facet_snapshot` | Within + facet + SnapshotOrder | Scan + size filter |
| `within_facet_natural_cold` | Cold index rebuild each iteration | Scan + facet + natural sort (no rebuild) |
| `zero_hits_natural_cold` | Cold index + zero-hit NaturalSort | Scan + natural sort on empty set |
| `exact_first` | Exact posting fast path | String equality |
| `glob_pdf` | FileMask `*.pdf` | fnmatch-style glob |
| `naive_within_scan` | (C# baseline in same harness) | Linear scan only |
| `naive_within_facet_natural` | (C# baseline) | Scan + facet + natural sort |

**SearchEngine.Sharp historical:** `sharp-v0.5.0` (first public release, no facet/glob), `sharp-v0.5.5` (audit baseline), `sharp-current` (selectivity pipeline on main).

Natural sort keys match `NaturalSortKeyBuilder` (ported in each language).

## Run locally (Windows x64)

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

Outputs CSV + environment JSON under `results/x64-win/`.

## Run on SSH hosts

```bash
# macOS / homelab — installs dotnet/go/node if missing (see scripts/install-deps.sh)
ssh macos 'bash -s' < scripts/run-remote.sh
ssh homelab 'bash -s' < scripts/run-remote.sh
```

## Single implementation

```bash
dotnet run -c Release --project benchmarks/SearchEngine.CompetitorBenchmarks/csharp -- \
  --implementation sharp-current --output results/x64-win

cd benchmarks/SearchEngine.CompetitorBenchmarks/rust && SE_BENCH_OUTPUT=../results/x64-win cargo run --release
cd benchmarks/SearchEngine.CompetitorBenchmarks/node && SE_BENCH_OUTPUT=../results/x64-win node bench.mjs
```

Historical Sharp via git worktree:

```powershell
./scripts/run-sharp-historical.ps1 -Tags v0.5.0,v0.5.5
```

Methodology: 3 warmup + 10 measured iterations; median reported in nanoseconds. Sharp cold workloads rebuild the index each iteration; competitors document `hot-corpus` in `notes`.
