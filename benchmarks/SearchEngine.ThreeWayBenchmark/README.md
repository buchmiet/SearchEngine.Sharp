# Three-way comparison: SearchEngine.Sharp, Lucene.NET, TantivyDotNet

One corpus, one set of questions, three engines, each in its own process. Run it with:

```bash
scripts/run.sh results        # needs the .NET 11 SDK; Tantivy's native library comes with the NuGet package
python3 scripts/aggregate.py results > results/report.md
```

| Engine | Version | Index used |
|---|---|---|
| SearchEngine.Sharp | this repository | in-memory snapshot, `SearchTokenization.Default` |
| Lucene.NET | 4.8.0-beta00017 | `RAMDirectory`, custom analyzer |
| TantivyDotNet | 1.0.0-rc.2 (Tantivy in Rust) | `SearchIndex.CreateInRam`, regex analyzer |

## What makes the comparison fair

- **Same documents.** The synthetic file-name corpus of the profiling harness (`Corpus.Generate`, seed 2026), 100,000
  and 1,000,000 names, document id = position.
- **Same tokens.** Lucene and Tantivy get an analyzer that splits at exactly the separators of
  `SearchTokenization.Default` and lower-cases. Because of that, **every query returns the same number of hits in all
  three engines** (the harness prints the table; a mismatch would show up there).
- **Same semantics per query.** `rep*` is a `PrefixQuery` in Lucene, a term regex in Tantivy and a glob in Sharp, and so on.
  Queries are built once for Lucene and Tantivy (their query objects are reusable); Sharp parses its expression
  string on every call, and that cost is inside its numbers.
- **Three result shapes**, because the engines are built around different ones:
  - *Count*: the number of hits, no ids.
  - *Top 10*: ten hits with their ids. Lucene and Tantivy rank by BM25 and return the best ten; Sharp has no ranking and
    no top-k, so it materialises the whole hit list and the harness reads ten ids from it.
  - *All ids*: every matching id in managed memory. Lucene collects document ids (equal to the ids here, as documents were added in
    id order); Tantivy has no bulk id export, so the harness uses the faster of two paths (sort by the fast `id` column, which
    returns ids as sort values, or stored-field reads) and reports the other path separately.
- **Timing.** Median of many single calls after a warm-up (150 ms, then 700 ms of samples, at least 15), one thread. Each
  table cell is the median over repeat runs (3 at 100k, 2 at 1M). Indexes are merged to one segment before the
  "merged" numbers (Sharp has no segments; Tantivy was already one segment with one indexing thread).
- **Not covered**: ranking quality, phrase queries in Sharp (not supported), natural sort and facets (Sharp only), many writer or
  reader threads, and article-sized documents.

## Caveats

- Run on a shared 4-vCPU Xeon VM (2.1 GHz, AVX2) under Ubuntu 24.04 with .NET 11.0.100-rc.3, not pinned to a CPU. Absolute numbers
  differ from the laptop numbers in `docs/performance.md`; the ratios are the useful part.
- Memory figures are process RSS growth after a build and a full GC. They include allocator slack and, for Lucene, the 256 MB
  writer buffer, so read them as rough.
- Sharp's `Count` for a plain term reads the posting-list length (O(1)), so that row measures a lookup, not a scan.
- Tantivy's index is reached through a native boundary; every search crosses it, and results travel as JSON.

## Sustained incremental indexing

`--mode sustained` measures what the table above cannot: the latency of every call while an index of 1,000,000 documents keeps
receiving documents, with the same visibility guarantee in all three engines (the call returns when a query sees the new
documents), ids validated, and the cost of durability measured apart.

```bash
scripts/run-sustained.sh results/sustained     # every engine and scenario, one process each
scripts/repeat-sustained.sh results/sustained 2 # two more runs of the single-document scenario
python3 scripts/report_sustained.py results/sustained   # report.md and the plots (needs matplotlib)
```

Scenarios: 20,000 single-document additions (without and with a concurrent reader), batches of 10, 100, 1,000 and 10,000, and a
durable disk-index run for the engines that have one. `PREV_DLL=<dll>` adds SearchEngine.Sharp built from another commit, as
the report does for the layout before segments. The report is [results/sustained/report.md](results/sustained/report.md);
`*.json` hold every call's latency, `*.log` the console output. The method and its caveats are in the report.
