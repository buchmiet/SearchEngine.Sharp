## Findings

All numbers below are from the tables in this report (1,000,000 documents, one VM, see Caveats).

1. **The 21 µs of the first benchmark was a property of the first 25 calls, not of sustained writing.** It came from 25 consecutive
   additions, when the old layout's delta held at most 25 documents. Over 20,000 additions the previous layout of SearchEngine.Sharp
   has a median of 1.09 ms, a p99.9 of 322 ms, a slowest call of 2.16 s and 9 calls over a second: the delta grows with every call and
   is rebuilt in full each time (the saw in the first plot), and the whole index is rebuilt about every 2,000 calls. That is the
   amortised cost the question was about, and it is the reason `IndexUpdater` now writes segments.
2. **Sustained, with segments:** SearchEngine.Sharp adds 20,000 documents in 1.0 s (20,700 documents/s): p50 33 µs, p99 144 µs,
   p99.9 0.64 ms, slowest 62 to 104 ms over three runs, which is one garbage collection (GC pauses 104 ms in the first run). Lucene.NET:
   1,650 documents/s, p50 449 µs, p99 2.9 ms. TantivyDotNet: 175 documents/s, p50 4.6 ms, p99 17.6 ms, p99.9 107 ms.
3. **The earlier "338×" over Tantivy was inflated by the harness.** The first benchmark created and closed a Tantivy writer for every
   addition (7.1 ms). Here one writer stays open for the run; the same operation takes 4.6 ms. The remaining gap to Sharp is structural
   rather than a tuning artefact: Tantivy makes a document visible only through a commit (a new segment) and a searcher refresh,
   which is 4 to 5 ms even into a RAM index; Lucene's near-real-time reader needs no commit.
4. **Write amplification** (documents written by merges per document added): SearchEngine.Sharp 6.6×, measured by the updater;
   Lucene.NET 104×, measured with a hook on its merge scheduler (2.1 million documents rewritten over 20,000 additions, 13 s of merge
   threads). TantivyDotNet has no merge hook, so only a lower bound on the number of merges is shown.
5. **Memory.** During the 20,000 additions the process RSS of SearchEngine.Sharp grew by 52 MiB and the managed allocations were
   226 MiB. Lucene.NET allocated 19,800 MiB (about 1 MB per addition, most likely its near-real-time reopen) with 768 ms of GC pauses.
   The previous layout allocated 19,800 MiB too and paused 21 s in garbage collections. TantivyDotNet allocates little managed memory
   (65 MiB) and holds its index in native memory: its absolute RSS is the smallest of the three (295 MiB before, 365 MiB at the peak).
6. **Batches.** From 10 to 10,000 documents per call SearchEngine.Sharp goes from 71,500 to 333,000 documents/s, Lucene.NET from
   6,700 to 165,000, TantivyDotNet from 1,800 to 104,000, the previous layout from 930 to 6,800. The advantage over Lucene.NET is 10.6×
   at 10 documents per batch and 2.0× at 10,000.
7. **Durability costs are separate and large for the engines that have it.** With a commit that reaches the disk after every document
   Lucene.NET takes 4.8 ms (against 0.45 ms in memory) and TantivyDotNet 17.3 ms (against 4.6 ms). SearchEngine.Sharp has no
   persistent index, so it has no number here; comparing it with a durable engine means comparing different guarantees.
8. **Readers during writes.** With one reader thread, SearchEngine.Sharp's median query is 237 µs against 149 µs in the previous layout
   and 208 µs in Tantivy (Lucene.NET 614 µs): queries now visit every segment (14 to 16 of them), which costs a little. The reader ran
   for the 1.1 s that the Sharp run lasted, so it has only 346 samples and its p99.9 and maximum should not be read as tails.
   The previous layout's reader saw a 636 ms stall during a rebuild.

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
  17 ms, 87 ms and 210 ms.
- RSS includes allocator slack, Lucene's RAM buffer and Tantivy's native memory, so differences of a few percent mean nothing and the
  engines start from different baselines.
- Only additions of new ids and no updates or deletions. Updates and deletions add dead documents and, for Sharp, kill-logging during
  merges; they are covered by the unit tests but not timed here.
- A single writer thread. Concurrent writers are serialised by Sharp's updater and are a different benchmark.
