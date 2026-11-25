# SearchEngine.Sharp.Profiling

Console harness used for the November 2026 profiling of the Commander find-name pattern.
Not part of the solution build; build it explicitly against the library in this repository:

```bash
dotnet build benchmarks/SearchEngine.Sharp.Profiling -c Release
```

Every mode prints JSON objects, one per line, to standard output. `--label TEXT` (where a mode reads it) is copied
into the output so several runs can share one log file. Run without arguments to list the modes.

| Mode | What it measures |
|------|------------------|
| `gen-tree --root DIR --count N [--seed S] [--no-meta]` | writes a deterministic synthetic tree. File sizes and modification times are set unless `--no-meta`; sizes are sparse (NTFS files are marked sparse first), so the logical sizes do not use disk space. |
| `find --variant A\|B\|C\|D\|E --root DIR --query Q [--regex] [--size-min B] [--size-max B] [--days D] [--instrument] [--hash] [--dump-hits FILE] [--label L]` | one Commander-style search: A = faithful copy of `InternalFindNameEngine`, B = same with `--policy adaptive\|fixed\|debounce\|single --batch N --interval MS`, C = single `FileSystemEnumerable` pass without an index, matching names with `NameMatcher`, D = C with parallel directory workers (`--threads`), E = A with a single enumeration pass |
| `decompose --root DIR --query Q [--reps N] [--label L]` | the same search split into sequential phases, plus one full FileMask build through the real `IndexSnapshotBuilder` (`full_buildMs`, `full_allocMB`, tokenization alone, and the first and second `Within` query, whose difference is the lazy bigram index) |
| `querypaths [--sizes 10000,100000,1000000] [--preset Default\|FileMask] [--only NAME] [--unicode FLAGS] [--seed S] [--label L]` | latency and allocation of every query path, both presets unless `--preset` is given; `--unicode` adds `UnicodeMatching` flags to the preset |
| `scale --n N [--preset FileMask\|Default] [--facets] [--unicode FLAGS] [--seed S] [--label L]` | build time, heap bytes per entry, GC, peak RSS (run one process per N) |
| `persistent --n N [--mutations M] [--label L]` | build, query, full-rebuild mutation cost, tiered base+delta+tombstone prototype |
| `unicode --dir DIR` | NFC/NFD and case-folding behaviour on the local file system. It writes its probe files into the new subdirectory `DIR/leith-unicode-probe`, refuses to run when that subdirectory already exists and is not empty, and deletes it when done; nothing else in `DIR` is touched. |
| `study --engine flat\|trigram\|tiered [--n N] [--threads T] [--seed S] [--label L]` | fast-changing-data study: build and query cost of the flat name buffer, the trigram index and the tiered engine |
| `concurrency [--n N] [--readers R] [--seconds S]` | readers during rebuilds, regex cache, ingestion cancellation, DI start-up |

Peak RSS comes from `getrusage` on Linux and macOS and from the peak working set on Windows.

Scripts in `scripts/`:

| Script | Purpose |
|--------|---------|
| `bench-find.sh BIN ROOT LABEL OUT.jsonl [REPS] [CPUSET] [full\|short]` | `find` matrix: variants A/B/C/D x queries x repetitions, one process per run |
| `bench-breakdown.sh BIN ROOT LABEL OUT.jsonl [CPUSET]` | instrumented variant A plus `decompose` for two queries |
| `tabulate.py KIND FILE...` | turns the JSONL logs into Markdown tables (`find`, `breakdown`, `querypaths`, `scale`, `persistent`) |
| `speedscope_top.py FILE [N]` | top inclusive and self frames of a speedscope profile |
| `apidiff.cs` | stand-alone file-based program: public API diff by reflection of two builds of the library |
