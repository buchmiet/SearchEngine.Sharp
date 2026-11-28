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

Scenarios: 20,000 single-document additions (without a reader, with a reader, and with a reader while the writer is paced at
`PACED_RATE` calls per second so every engine runs under the same write load), batches of 10, 100, 1,000 and 10,000, and a durable
disk-index run for the engines that have one. Reader queries are tagged when a background merge runs (Sharp, Lucene) and when they
overlap a write call over 10 ms; merges are counted and, for Lucene, timed one by one. `ONLY="single single-reader-paced"` runs
a subset. `PREV_DLL=<dll>` adds SearchEngine.Sharp built from another commit, as
the report does for the layout before segments. The report is [results/sustained/report.md](results/sustained/report.md);
`*.json` hold every call's latency, `*.log` the console output. The method and its caveats are in the report.

## Index lifecycle: additions, updates and deletions

`--mode lifecycle` is the sustained benchmark with the whole life of an index: an index of n documents (ids 0..n-1) receives one long stream of
single-document additions, updates and deletions, every call timed until the change is visible to a query (same guarantee as above: Sharp
`AddEntry` / `RefreshEntry` / `RemoveEntry` publish synchronously; Lucene.NET `AddDocument` / `UpdateDocument(term)` / `DeleteDocuments(term)` +
`MaybeRefreshBlocking`; Tantivy `AddDocument` / `UpdateDocument(term)` / `DeleteTerm` + commit + searcher refresh; in-memory indexes).

```bash
scripts/run-lifecycle.sh results/lifecycle            # four configurations, every engine, one process each (~70 minutes, see the script header)
python3 scripts/report_lifecycle.py results/lifecycle # report.md and the plots (needs matplotlib)
# one run by hand
dotnet bin/Release/net11.0/SearchEngine.ThreeWayBenchmark.dll --mode lifecycle --engine sharp --n 200000 --ops 120000 \
  --mix add:20,update:60,delete:20 --old-share 0.9 --check-every 24000 --out sharp-main.json
```

| Option | Meaning |
|---|---|
| `--engine sharp\|lucene\|tantivy`, `--label` | engine (one per process) and the name shown in the report |
| `--n`, `--ops` | starting documents and number of operations |
| `--mix add:30,update:50,delete:20` | weights of the operation types (default as shown) |
| `--old-share 0.9` | share of updates and deletions that hit an original document no operation has touched (the large first segment); the rest hit a live document among the last `--recent-window` (default 2,000) writes |
| `--reader 1 --rate R` | a reader thread cycles three queries; the writer is paced at R calls per second (0 = unpaced) |
| `--check-every K` | untimed light validation every K operations (counts and a sample of version tokens); the full check always runs at the end |
| `--full-ids 0` | skip the id list of every live document at the end (the live count and all other id sets are still compared); for 1,000,000 documents on Tantivy |
| `--seed`, `--out` | seed of the corpus and of the operation stream, output JSON |
| `--self-test-skip K` | validation self-test: every K-th update is not sent to the engine, the checks must then report errors |

What is measured: latency per operation type and overall (mean, p50, p95, p99, p99.9, max), throughput; the layout after every call (segments, documents
outside the largest segment, dead documents in all segments and in the largest one) and every moment a different segment becomes the largest one
(Sharp: reference of the segment object, Lucene: segment name, Tantivy: segment id), with the latency of the surrounding calls; merges (count, documents written,
time; Lucene's every merge); peak RSS, managed allocations, GC pauses; with a reader, reader latency tagged by merges and slow writes.
Validation: the expected index is kept in a model and compared by ids with every engine (a token every live document has, per-version tokens with
only the latest matching, tokens of deleted documents, `jpg`, `final`, `report AND pdf`, `rep*`); the report shows the mismatches, all of which must be zero. The method, the
schema change (Lucene.NET indexes the id as a `StringField`, Tantivy's `id` field is now indexed, which also applies to the base build of the other scenarios) and the
caveats are in [scripts/lifecycle-notes.md](scripts/lifecycle-notes.md), which the report includes.
