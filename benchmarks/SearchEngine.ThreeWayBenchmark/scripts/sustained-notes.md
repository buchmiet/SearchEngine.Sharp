## Findings

All numbers below are from the tables in this report (1,000,000 documents, one VM, see Caveats). "First run" is the run the plots show.

1. **The 21 µs of the first benchmark was a property of the first 25 calls, not of sustained writing.** It came from 25 consecutive
   additions, when the old layout's delta held at most 25 documents. Over 20,000 additions the previous layout of SearchEngine.Sharp
   has a median of 1.1 to 1.3 ms, a p99.9 of 323 to 369 ms, a slowest call of 1.9 to 2.5 s and 9 calls over a second in every run: the
   delta grows with every call and is rebuilt in full each time (the saw in the first plot), and the whole index is rebuilt about every
   2,000 calls. That is the amortised cost the question was about, and it is the reason `IndexUpdater` now writes segments.
2. **Sustained, with segments:** SearchEngine.Sharp adds 20,000 documents in 0.9 to 1.0 s (about 21,000 documents/s): p50 25 to 32 µs,
   p99 148 to 229 µs, slowest call 62 to 104 ms over three runs, which is one garbage collection (101 ms of GC pauses in the first run).
   Lucene.NET: about 1,800 documents/s, p50 407 to 466 µs, p99 2.5 to 3.1 ms. TantivyDotNet: about 190 documents/s, p50 4.3 to 4.4 ms,
   p99 13 to 22 ms, p99.9 107 ms.
3. **The earlier "338×" over Tantivy was inflated by the harness.** The first benchmark created and closed a Tantivy writer for every
   addition (7.1 ms). Here one writer stays open for the run; the same operation takes 4.3 ms. The remaining gap to Sharp is structural
   rather than a tuning artefact: Tantivy makes a document visible only through a commit (a new segment) and a searcher refresh,
   4 to 5 ms even into a RAM index; Lucene's near-real-time reader needs no commit.
4. **Merge frequency, duration and write amplification** (first single run, writer as fast as it goes):
   - SearchEngine.Sharp: 8,420 merges, 421 per 1,000 additions, 52 µs each on average (0.44 s in all), 6.6 documents rewritten per
     document added. 5,168 of them ran inside the write call: at 21,000 additions per second the writer outran the background merges and
     hit the 64-segment limit. At 120 additions per second (the paced run) all 6,664 merges ran in the background, none in the write call.
     The updater counts merges and their total time but does not publish single merges, so there is no per-merge distribution.
   - Lucene.NET (hook on its merge scheduler): 2,220 merges, 111 per 1,000 additions; one merge p50 1.1 ms, p95 36 ms, max 95 ms;
     12.2 s of merge threads; 97 documents rewritten per document added. In the paced run the same 2,221 merges rewrote 21 million
     documents (1,057×): with the default `TieredMergePolicy`, 1,604 merges of more than 5,000 documents rewrote the growing segment
     that holds the added documents again and again. The policy was not tuned.
   - TantivyDotNet exposes no merge hook: at least 83 merges (falls of the segment count between samples), no durations.
5. **Memory** (first single run). Process RSS grew by 50 MiB for SearchEngine.Sharp (251 MiB of managed allocations, 101 ms of GC
   pauses). Lucene.NET allocated 19,400 MiB, about 1 MB per addition, with 696 ms of GC pauses; in the paced run 88,500 MiB and 6.7 s
   of pauses, following its merges. The previous layout allocated 19,800 MiB and paused 21.9 s in garbage collections. TantivyDotNet
   allocates little managed memory (62 MiB) and holds its index in native memory; its absolute RSS is the smallest (292 MiB before,
   361 MiB at the peak, against 408 and 458 MiB for Sharp).
6. **Search during indexing and merges, same write load** (120 additions per second for 167 s, one reader thread):
   - SearchEngine.Sharp: 70,100 queries, p50 139 µs, p99 4.9 ms, max 42 ms. Its merges are so short that only 83 queries started
     during one, and those ran like the others (p50 137 µs). The p99 is the `rep*` prefix query, a third of the queries, with or
     without merges (the three bands of the plot are the three queries).
   - Previous layout: p50 141 µs, max 689 ms; 4,456 queries overlapped a write call over 10 ms, and those reach 0.69 s: the rebuilds
     stall readers on this 4-core machine.
   - Lucene.NET: p50 457 µs, p99 1.4 ms, max 66 ms; a merge ran during 48,241 of its 101,600 queries, at about the same latency
     (p50 469 µs during, 448 µs without).
   - TantivyDotNet: p50 204 µs, p99 1.8 ms, max 20 ms; merges are not observable.
   The unpaced reader table further down ran for as long as each writer (1 s for Sharp, 107 s for Tantivy), so its sample counts and
   tails are not comparable; the paced table is the comparison.
7. **Batches.** From 10 to 10,000 documents per call SearchEngine.Sharp goes from 71,500 to 333,000 documents/s, Lucene.NET from
   6,700 to 165,000, TantivyDotNet from 1,800 to 104,000, the previous layout from 930 to 6,800. The advantage over Lucene.NET is 10.6×
   at 10 documents per batch and 2.0× at 10,000.
8. **Durability costs are separate and large for the engines that have it.** With a commit that reaches the disk after every document
   Lucene.NET takes 4.8 ms (against 0.41 ms in memory) and TantivyDotNet 17.3 ms (against 4.3 ms). SearchEngine.Sharp has no
   persistent index, so it has no number here; comparing it with a durable engine means comparing different guarantees.

## Method

- **Same visibility guarantee.** `AddVisible` returns when a query on the freshest state sees every added document. Sharp: the call
  returns after publishing a snapshot. Lucene.NET: add, then `SearcherManager.MaybeRefreshBlocking` (near real time, no commit).
  TantivyDotNet: add, `Commit`, `SearcherManager.Refresh`. All three are in-memory indexes; the durable scenario adds a commit to a
  directory for Lucene.NET and Tantivy.
- **Same tokens and documents.** As in the first three-way run: Lucene and Tantivy analysers split at the separators of Sharp's
  `Default` preset. Added documents are `<corpus name> freshdoc zqtok<id>x`.
- **Validation by id.** After every run the sorted ids of four result sets are hashed (see Verification). Each call also checks that the
  probe token of the newest document has exactly one hit, and every 97th call that the hit is that document.
- **Timing.** One call of `AddVisible` per sample (`Stopwatch`), nothing else in the timed region; layout and merge statistics are read
  between calls. Process RSS is sampled every 20 ms; managed allocations and GC pauses come from the runtime counters.
- **Merges.** Sharp: the updater's own counters (count, documents written, total time), read through reflection, and its list of running
  merges for tagging queries. Lucene.NET: a `ConcurrentMergeScheduler` subclass that times every merge and counts the running ones.
  TantivyDotNet: falls of the segment count between samples only.
- **Paced run.** The writer issues one call every 1/120 s (it never fell behind: 120 calls per second reached by every engine), so all
  readers run 167 s under the same write load. The reader sleeps 1 ms between queries. A query is tagged "during a merge" when a
  background merge was running as it started, and "overlapping a slow write" when it overlapped a write call of more than 10 ms.
- **Tantivy writer.** The bulk build uses a 256 MB writer. For the additions the bulk writer is closed and a writer with a 50 MB heap
  and one thread takes over, because the large heap made every small commit slower (11 ms at 100,000 documents with 256 MB). Sweep on
  the 1,000,000-document index, 1,000 additions: 15 MB p50 4.6 ms, 30 MB 4.5 ms, 50 MB 4.0 ms (two threads need a larger minimum heap and
  did not start). Merge policy and other settings were left at their defaults.
- **Lucene.NET.** `TieredMergePolicy` and a `ConcurrentMergeScheduler` (subclassed only to count merges), 256 MB RAM buffer.
- **Previous layout of SearchEngine.Sharp.** The same benchmark built against the commit before `IndexUpdater` wrote segments
  (`2e74819`); the adapter reads the size of the delta through reflection because that layout has no segments.

## Caveats

- One 4-vCPU Xeon VM (2.1 GHz), not pinned, with the benchmark and the engines' background threads sharing the cores. Absolute numbers
  differ on other machines; the shapes (the saw, the flat lines, the ratios) are what the plots are for.
- One run per cell, except the single-document scenario (three runs, listed). Tails move between runs: Lucene.NET's slowest call was
  17 ms, 19 ms and 210 ms. The first single run and the paced runs were made after the other cells, with the same build of every
  engine; the batch, durable and unpaced-reader cells are from the earlier pass.
- RSS includes allocator slack, Lucene's RAM buffer and Tantivy's native memory, so differences of a few percent mean nothing and the
  engines start from different baselines.
- Only additions of new ids and no updates or deletions. Updates and deletions add dead documents and, for Sharp, kill-logging during
  merges; they are covered by the unit tests but not timed here.
- A single writer thread. Concurrent writers are serialised by Sharp's updater and are a different benchmark.
