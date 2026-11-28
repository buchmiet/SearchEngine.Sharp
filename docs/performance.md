# Performance and tuning

What a query, a build and a progressive scan cost, which knobs change that, and how to measure it on your own
machine.

## Test machine and data

Unless a section says otherwise, the numbers come from one machine:

- Intel Core i7-10750H (x64 laptop CPU), Ubuntu 26.04, .NET 11.0.0, default JIT settings (tiered compilation with
  dynamic PGO).
- One process pinned to one logical CPU (`taskset -c 5`), so the numbers show single-thread cost.
- The synthetic file-name corpus of the profiling harness (`--seed 2026`): camera images, source files, documents,
  libraries, hashes, logs and 12% non-Latin names. Every entry carries the facets `size`, `modified` and `attrs`.

Other machines differ by a roughly constant factor; the ratios between query kinds stay similar.
[Reproducing the numbers](#reproducing-the-numbers) lists the commands.

## Query latency

Median latency of one `Find` call on a snapshot that already exists, including the result list. Hits are the
number of returned ids.

`SearchTokenization.Default` (token-level search; 100,000 names give 39,000 unique tokens, 1 million give
304,000):

| Query | Method | Hits at 10^5 | Time at 10^5 | Hits at 10^6 | Time at 10^6 |
|---|---|---:|---:|---:|---:|
| `zzzqqq` (no match) | Exact | 0 | 1.4 µs | 0 | 8.6 µs |
| `final` | Exact | 1,291 | 21 µs | 13,505 | 165 µs |
| `jpg` (10% of all names) | Exact | 10,555 | 64 µs | 105,458 | 633 µs |
| `report AND pdf` | Exact, operators | 29 | 25 µs | 228 | 75 µs |
| `jpg OR png` | Exact, operators | 15,831 | 91 µs | 157,960 | 737 µs |
| `(jpg OR png) AND NOT img` | Exact, operators | 15,831 | 96 µs | 157,960 | 734 µs |
| `q` (one character) | Within | 3,175 | 362 µs | 32,634 | 3.3 ms |
| `re` | Within | 14,371 | 148 µs | 143,487 | 1.5 ms |
| `rep` | Within | 1,350 | 92 µs | 13,262 | 669 µs |
| `report` | Within | 1,300 | 122 µs | 12,837 | 828 µs |
| `rep AND pdf` | Within, operators | 42 | 98 µs | 305 | 564 µs |
| `rep*` | glob | 943 | 180 µs | 9,313 | 1.7 ms |
| `*ing` | glob | 903 | 284 µs | 9,030 | 2.7 ms |
| `*port*` | glob | 2,594 | 502 µs | 25,755 | 4.6 ms |
| `dsc?????` | glob | 3,955 | 201 µs | 40,043 | 1.5 ms |
| `^rep.*t$` | Regex | 733 | 5.7 ms | 7,321 | 15 ms |
| `\d{4}` | Regex | 35,635 | 10.7 ms | 356,181 | 38 ms |
| `pdf` + size ≥ 100 MiB | Exact, filter | 0 | 39 µs | 2 | 213 µs |
| `report` + modified in the last 30 days | Within, filter | 179 | 160 µs | 1,609 | 941 µs |
| empty + size ≥ 1 GiB | filter only | 1 | 192 µs | 1 | 1.4 ms |
| empty + "not a directory" (every name) | filter only | 100,000 | 464 µs | 1,000,000 | 4.4 ms |
| `jpg`, natural sort | Exact | 10,555 | 4.3 ms | 105,458 | 65 ms |
| `report`, natural sort | Within | 1,300 | 670 µs | 12,837 | 9.2 ms |

`SearchTokenization.FileMask` (the whole name is one token, so the vocabulary is the set of distinct names: 78,000
at 10^5 and 659,000 at 10^6). Every query also applies the facet filter "not a directory", as a file search
dialog would. The 10^6 column is the median of three runs:

| Query | Method | Hits at 10^5 | Time at 10^5 | Hits at 10^6 | Time at 10^6 |
|---|---|---:|---:|---:|---:|
| `package.json` | Exact | 1,319 | 47 µs | 13,308 | 0.13 ms |
| `img_*` | glob | 4,037 | 498 µs | 40,244 | 5.5 ms |
| `dsc?????.jpg` | glob | 3,955 | 338 µs | 40,043 | 3.1 ms |
| `*.pdf` | glob | 4,869 | 730 µs | 47,842 | 7.8 ms |
| `*.log` + size ≥ 1 MiB | glob, filter | 59 | 647 µs | 565 | 7.2 ms |
| `*report*` | glob | 1,300 | 1.1 ms | 12,837 | 10.9 ms |
| `*.jpg OR *.png` | glob, operators | 15,831 | 1.3 ms | 157,960 | 14.6 ms |
| `*.cs AND NOT *tests*` | glob, operators | 3,790 | 1.7 ms | 38,508 | 17.2 ms |
| `*` | glob | 100,000 | 585 µs | 1,000,000 | 6.3 ms |
| `*` + modified in the last 30 days | glob, filter | 12,716 | 591 µs | 126,335 | 5.8 ms |
| `report` | Within | 1,300 | 338 µs | 12,837 | 4.7 ms |
| `\.(jpg\|png)$` | Regex | 15,831 | 26 ms | 157,960 | 64 to 228 ms |

The regex time at 10^6 varied between runs by a factor of 3.5 (the JIT optimizes the regex loop differently from
run to run); the other rows varied by less than 15%.

What the tables show:

- Exact terms and boolean operators on exact terms cost microseconds to a millisecond. Their cost grows with the
  number of hits, not with the size of the index.
- Globs, regexes and one-character `Within` terms read the whole vocabulary. With `FileMask` the vocabulary is
  every distinct name, so these queries cost about 1 ms per 100,000 names for globs and much more for regexes.
- A regex is the slowest way to express a query. Use a glob when one can express it.
- The result list costs 4 bytes per hit; queries allocate little else.

## Cost model

### Building and updating

| Operation | Cost | Measured at 10^5 | Measured at 10^6 |
|---|---|---:|---:|
| Full build, `Default` | linear in the number of tokens | 0.21 s | 2.0 s |
| Full build, `FileMask` | linear in the number of names | 0.19 s | 2.1 s |
| `RebuildFrom` | one full build | same | same |
| `AddEntry`, `RemoveEntry`, `RefreshEntry`, `AddOrUpdateEntries`, `RemoveEntries` | a build of the entries changed since the last full build (the delta), or a full build when the changes pass the merge limit | see below | see below |

### Keeping an index current

A snapshot of an `IndexUpdater` has three parts: a base built from all entries, a delta built from the entries added
or replaced since then, and the list of base documents removed or replaced since then. A change keeps the base of
the previous snapshot and builds only a new delta, so its cost grows with the number of changes since the last full
build, not with the size of the index. When the delta and the removed base documents together pass about
`2 × √(entry count)`, the change builds the whole index instead (a merge), and the next changes start from an empty
delta again:

| Entries | Merge limit | Largest delta a change builds | Changes between two merges |
|---:|---:|---:|---|
| up to 16,000 | 256 | 256 | 128 to 256 |
| 10^5 | 632 | 632 | 316 to 632 |
| 10^6 | 2,000 | 2,000 | 1,000 to 2,000 |
| 4.2 × 10^6 and more | 4,096 | 4,096 | 2,048 to 4,096 |

Adding an entry counts one (its delta entry), replacing an entry of the base counts two (the new delta entry
and the removed base document), removing one counts one, and changing an entry that is already in the delta
again counts nothing. A merge costs a full build (0.2 s at 10^5, 2 s at
10^6). One call with many changes publishes once: a batch larger than the merge limit is a full build, which is
what `ProgressiveIndexIngestion` does with its large batches. Queries never wait for a change: they keep reading
the previous snapshot until the new one is published. The async rebuild methods read their input before taking
the updater's lock, so other writers wait only for the build itself.

Every query pays a little for the delta and the removed documents: the delta is searched like a second, small
index, and the removed documents are cleared from the result once.

### Memory

Measured after one full build with facets (one process per size):

| | `Default` 10^5 | `FileMask` 10^5 | `Default` 10^6 | `FileMask` 10^6 |
|---|---:|---:|---:|---:|
| Snapshot, bytes per entry | 131 | 175 | 122 | 160 |
| Entries kept by the updater (names, sort texts, facets) | 25 MB | 25 MB | 252 MB | 252 MB |
| Allocated during the build | 62 MB | 45 MB | 588 MB | 383 MB |
| Peak process memory (RSS) | 107 MB | 120 MB | 642 MB | 771 MB |

Plan for about 400 bytes per indexed name with three facets (snapshot plus the updater's copy of the entries) and
about 0.8 KB per name of peak memory while a build runs, because the old snapshot stays alive until the new one is
published. Each facet adds 8 bytes per entry to the snapshot. The bigram index for `Within` and the natural sort
permutation are extra and exist only after their first use (see below).

### Work deferred to the first query

Two structures are built on first use. Concurrent first callers wait for one build; every later query reuses it.
They belong to the base of a snapshot, so a change that keeps the base keeps them too: their cost comes back only
after a full build (`RebuildFrom` or a merge). The delta has its own small ones, built again after every change by
the first query that needs them; a natural sort also places each delta document in the base permutation then.

| Structure | Built by | Measured at 10^5 | Measured at 10^6 |
|---|---|---:|---:|
| Bigram index | the first `Within` query with a term of two or more characters | 18 ms (109,000 tokens) to 90 ms (78,000 whole names) | 0.12 s (1 million tokens) to 0.46 s (659,000 whole names) |
| Natural sort permutation | the first `NaturalSortAscending` query whose result holds more than 80% of the documents | 0.18 s | 2.2 s |

The permutation was measured on `SyntheticPathFeed` paths (6 to 7 tokens per sort text); the bigram index on both
corpora. `Exact`, glob and regex queries never build the bigram index.

### Per query

- An exact term that is the whole expression (also with `enableOperators: true`), with `SnapshotOrder`, reads the
  term's posting list directly, and a facet filter is checked only on those postings. Any other query evaluates
  into a bitset with one bit per document, so it pays at least a pass over N/64 words.
- `Within` looks up candidate tokens by the first two characters of the term and searches each candidate with a
  vectorized `IndexOf`. A one-character term scans the vocabulary.
- A glob term scans the vocabulary; tokens shorter than the pattern's minimum length are skipped. Patterns of the
  shapes `abc*`, `*abc`, `*abc*` and `ab*cd` (no `?`) use vectorized string search.
- A regex runs `Regex.IsMatch` on every token of the vocabulary.
- A facet filter checks only the hits when they are at most 75% of the documents, otherwise it scans the facet
  columns. A filter-only query always scans the columns and is then sorted like any other result.
- `NaturalSortAscending` with one hit costs nothing extra. While the snapshot has no permutation yet, a result of
  up to 80% of the documents builds the sort keys of its hits and sorts them (65 ms for 105,000 hits at 10^6), and
  a larger result builds the permutation and walks it. Once the permutation exists, every result of more than 0.5%
  of the documents walks it, and only smaller results sort their hits.

When you re-run a query after every publish and sort it naturally, each run pays the sort of its hits until
some query builds the permutation, and a dense result pays that build once per full build of the index.

## Index or plain scan

Building an index pays off only when the same names are queried many times. The measurement below searches a
synthetic tree on disk (ext4, warm cache) for `*.pdf` (one process per run, one core):

| Tree | Plain scan with `NameMatcher` | Scan into a progressive index, query after every publish |
|---|---|---|
| 100,000 files | 0.12 s, 2.6 MB allocated, first hit after 16 ms | 1.4 s, 190 MB allocated, first hit after 95 ms |
| 1,000,000 files | 1.15 s, 27 MB allocated, first hit after 16 ms | 13 to 15 s, 1.7 to 1.9 GB allocated, 1.1 GB peak memory, 14 publishes, first hit after 112 ms |

Both variants read each directory entry once with `FileSystemEnumerable`, as in the recipes of the
[file search guide](file-search-guide.md#index-or-plain-scan). A kept index answers a query over 1 million names
in 0.1 to 17 ms (a regex up to 230 ms) instead of a 1.15 s rescan. Building it from disk costs about as much as
5 to 9 rescans, so it pays off after about 5 to 9 queries on a tree that does not change in between. A change to
the kept index builds only the entries changed since the last full build, and the whole index (about 2 s at 10^6)
is built again once every 1,000 to 2,000 changes at 10^6.

## Tuning progressive ingestion

`ProgressiveIndexIngestion` buffers the entries of a scan and publishes them through
`IIndexUpdater.AddOrUpdateEntries`. A publish whose batch is larger than the merge limit of the updater (see
[Keeping an index current](#keeping-an-index-current)) is a full rebuild. The batches of a fast scan are that
large, so the number of publishes decides the total cost, and the time between publishes decides how stale the
index is.

### Policies

The policy is a case of `IngestPublishPolicy`, set in `IngestPublishOptions.Policy`. Each case has only the
settings it uses (the full list with defaults is in [the API reference](api.md#ingestpublishpolicy)).

| Policy | Publishes when | Use it for |
|---|---|---|
| `Adaptive` (default) | the buffer reaches the batch cap; or `max(MinInterval, PublishMultiplier × last publish duration)` has passed and at least `MinTimerPublishBatch` entries wait; or the oldest entry has waited `MaxStaleness`. A timer drives the time-based publishes | scans of any speed and size |
| `TimeDebounce` | `MinInterval` has passed since the last publish and the buffer is not empty; or the oldest entry has waited `MaxStaleness`. A timer drives both. The number of buffered entries never starts a publish | scans where you want a fixed refresh rate |
| `FixedBatch` | the buffer reaches `BatchSize`; or an entry arrives while the oldest buffered entry has waited `MaxStaleness`. No timer, so an idle scanner leaves entries unpublished | tests and benchmarks |
| `PerEntry` | after every entry | a baseline only: every publish builds the entries changed since the last full rebuild again, and the index is rebuilt every few hundred to few thousand entries |

100,000 synthetic paths fed as fast as possible (`--ingestion-policy` benchmark, same machine):

| Policy | Publishes | Total publish time | Publish time relative to one build | Longest observed staleness |
|---|---:|---:|---:|---:|
| `Adaptive` (defaults) | 7 | 0.62 s | 2.3 times | 0.25 s |
| `Adaptive` with `GrowthAwareBatchCap = false` | 50 | 7.1 s | 25 times | 1.5 s |
| `TimeDebounce`, 100 ms | 2 | 0.40 s | 1.5 times | 0.24 s |
| `FixedBatch`, 2,000 | 50 | 9.9 s | 36 times | 1.7 s |

### Options

These are the settings of `Adaptive`, except where noted.

- `GrowthAwareBatchCap` (default `true`) raises the batch cap after each publish to the number of
  indexed documents. A fast scan then publishes at 2k, 4k, 8k, 16k and so on, which keeps the total rebuild work
  near twice one full build. Leave it on unless you need publishes of a constant size.
- `BatchCap` (default 2,000) is the first batch cap. `FixedBatch.BatchSize` (default 2,000) is the size of every
  batch under `FixedBatch`. Smaller values show the first results sooner and cost more rebuilds.
- `MinInterval` (default 100 ms) is the shortest time between timer publishes. `TimeDebounce` has the same
  setting.
- `PublishMultiplier` (`k`, default 2) spaces timer publishes by `k` times the duration of the last publish, so
  while the timer drives the publishes about `1 / (1 + k)` of the time goes into publishing. Raise it to spend less
  CPU on publishing; lower it for fresher results.
- `MinTimerPublishBatch` (default 200) keeps a slow scan from publishing for a handful of new entries. Staleness
  overrides it.
- `IngestPublishOptions.ChannelCapacity` (default 10,000, any policy) bounds the queue between scanner and
  publisher. When it is full the scanner waits.
- `IngestPublishOptions.FlushOnCancellation` (default `true`, any policy) publishes everything accepted when the
  scan is cancelled.

### `MaxStaleness` and the worst case

`MaxStaleness` (default 1 s) is the longest an accepted entry should wait before the publish that contains it
starts. That publish's own duration comes on top, as does time spent in `Published` and `onPublished` handlers.
`Adaptive` and `TimeDebounce` enforce it with a timer, so it holds while the scanner is idle. `FixedBatch`
publishes for staleness only when the next entry arrives, never from a timer: an idle scanner leaves a stale
buffer unpublished until it hands over another entry or ends. `PerEntry` has no `MaxStaleness`, because it
publishes every entry as soon as it arrives.

Under `Adaptive`, a staleness publish also waits until `k × R` has passed since the previous publish ended,
where `R` is the duration of that publish and `k` is `PublishMultiplier`. An entry that arrives just as a publish
starts therefore waits for that publish and the gap after it:

```
worst staleness = max(MaxStaleness, (1 + k) × R)
```

While `R < MaxStaleness / (1 + k)` (333 ms with the defaults, roughly 150,000 to 200,000 names on the test
machine) the bound is `MaxStaleness`. Beyond that it grows with the index: a 400 ms publish gives 1.2 s, a 2 s
publish at 1 million names gives 6 s. In exchange the number of publishes grows slowly with the scan size:
14 publishes for 1 million files in the table above.

`StrictMaxStaleness = true` publishes as soon as the oldest entry exceeds `MaxStaleness`, even right after a
publish. The bound is then `max(MaxStaleness, R)`. But once a publish takes longer than `MaxStaleness`, every
batch is already stale when the previous publish ends, the publisher rebuilds back to back, and the total work
grows with the square of the scan size: a 1 million file scan then takes 57 to 74 full rebuilds, about 2 minutes
of CPU and 30 GB of allocations. Use it only for small indexes that need a hard staleness bound.

Debounce user keystrokes separately (150 to 300 ms) from ingestion; the two delays are independent.

## Reproducing the numbers

The profiling harness is not part of the solution build. See [benchmarks/README.md](../benchmarks/README.md) for
every benchmark project.

```bash
dotnet build benchmarks/SearchEngine.Sharp.Profiling -c Release
P="dotnet benchmarks/SearchEngine.Sharp.Profiling/bin/Release/net11.0/SearchEngine.Sharp.Profiling.dll"

# Query latency tables
taskset -c 5 $P querypaths --sizes 100000,1000000

# Build time, memory per entry, cost of one mutation (one process per size and preset)
taskset -c 5 $P scale --n 1000000 --preset FileMask --facets

# Index or plain scan: generate a tree, then scan it both ways
$P gen-tree --root /tmp/tree1m --count 1000000 --seed 2026
taskset -c 5 $P find --variant scan --root /tmp/tree1m --query '*.pdf'
taskset -c 5 $P find --variant commander-single-pass --root /tmp/tree1m --query '*.pdf' --instrument

# Publish policies
taskset -c 5 dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000
```

On Windows and macOS drop `taskset`; the numbers then include scheduling noise from other cores.
