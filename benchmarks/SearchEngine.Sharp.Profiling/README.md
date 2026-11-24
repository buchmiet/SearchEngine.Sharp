# SearchEngine.Sharp.Profiling

Console harness used for the November 2026 profiling of the Commander find-name pattern.
Not part of the solution build; build it explicitly:

```bash
dotnet build benchmarks/SearchEngine.Sharp.Profiling -c Release [-p:NewFacets=true] [-p:UnicodeOpt=true]
```

`NewFacets` uses `FacetValues.Create(pairs)` (branch `compact-facet-values`), `UnicodeOpt` enables
`--unicode N` (branch `unicode-text-normalization`). Every mode prints one JSON object per line.

| Mode | What it measures |
|------|------------------|
| `gen-tree --root DIR --count N --seed S` | writes a deterministic synthetic tree (sparse files with size and mtime) |
| `find --variant A\|B\|C\|D\|E --root DIR --query Q [--regex] [--size-min B] [--days D] [--instrument] [--hash]` | one Commander-style search: A = faithful copy of `InternalFindNameEngine`, B = same with `--policy adaptive\|fixed\|debounce\|single --batch N`, C = single `FileSystemEnumerable` pass without an index, D = C with parallel directory workers (`--threads`), E = A with a single enumeration pass |
| `decompose --root DIR --query Q` | the same search split into sequential phases, plus the internal phases of one snapshot build |
| `querypaths --sizes 10000,100000` | latency and allocation of every query path, both presets |
| `scale --n N --preset FileMask\|Default [--facets]` | build time, heap bytes per entry, GC, peak RSS (one process per N) |
| `persistent --n N` | build, query, full-rebuild mutation cost, tiered base+delta+tombstone prototype |
| `unicode --dir DIR` | NFC/NFD and case-folding behaviour on the local file system |
| `concurrency --n N` | readers during rebuilds, regex cache, ingestion cancellation, DI start-up |

Scripts in `scripts/` run the matrices (`bench-find.sh`, `bench-breakdown.sh`, `bench-variants.sh`,
`run-lead-x64.sh`, `mac-real.sh`); see the header comment of each for arguments.
