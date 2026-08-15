# Library benchmark harness (0.5.6)

Compares **search libraries** on a shared in-memory file-name corpus (`FileSearchDataFactory` @ 100k, seed 2026).

This harness is **not** a cross-language runtime shootout. Hand-rolled reference algorithms in other languages were removed — they do not represent real libraries and are misleading.

## Currently measured

| Library | Versions | Notes |
|---------|----------|-------|
| **SearchEngine.Sharp** | `sharp-0.5.0-initial` @ `1bd312c`, `sharp-0.5.5`, `sharp-current` | Same workloads; historical via git worktree (x64) |

## Planned (not yet implemented)

External in-memory / embedded search libraries on equivalent workloads, e.g.:

- **Rust:** Tantivy (in-memory index)
- **JavaScript:** MiniSearch or FlexSearch
- **Go:** Bleve (in-memory)
- **C++:** (TBD — e.g. embedded full-text component with comparable semantics)

Each adapter must map the same queries (Within, Exact, FileMask glob, facet range, NaturalSort) and report verified `hit_count`.

## Workloads

See [`workloads.json`](workloads.json). Methodology: 3 warmup + 10 measured iterations; median ns. Cold workloads rebuild the index each iteration.

## Run

```powershell
cd benchmarks/SearchEngine.CompetitorBenchmarks
./scripts/run-all.ps1
```

Historical Sharp (requires git worktree):

```powershell
./scripts/run-sharp-historical.ps1
```

Single version:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.CompetitorBenchmarks/csharp -- \
  --implementation sharp-current --output results/x64-win
```

Evidence CSV: `audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/competitor-benchmarks/`.
