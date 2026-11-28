## Method

- **Scenario.** An index of n documents (ids 0..n-1) receives one long stream of single-document operations: additions of new ids (n, n+1, ...),
  updates of a live id and deletions of a live id, in a configurable mix (`--mix add:30,update:50,delete:20`). Every call returns only
  when the change is visible to a query, with the guarantee of the other scenarios: Sharp publishes a snapshot synchronously
  (`AddEntry`, `RefreshEntry`, `RemoveEntry`); Lucene.NET `AddDocument` / `UpdateDocument(term)` / `DeleteDocuments(term)` followed by
  `SearcherManager.MaybeRefreshBlocking` (near real time, no commit); TantivyDotNet `AddDocument` / `UpdateDocument(term)` / `DeleteTerm`
  followed by `Commit` and `SearcherManager.Refresh`. All three are in-memory indexes, durability is not part of this scenario.
- **Which documents are updated and deleted.** With probability `--old-share` an update or deletion hits an original document that no operation
  has touched yet (so it lives in the large first segment and leaves dead documents there); otherwise a live document among the last
  `--recent-window` writes (it lives in the small, young segments). That is how the run exercises dead documents in the largest segment, merges of
  segments that contain deletions and, once enough original documents are dead, the rewrite of the largest segment (SearchEngine.Sharp rewrites a
  segment when 30% of its documents are dead, `MergePolicy.DeadRatio`; Lucene.NET's `TieredMergePolicy` and Tantivy's `LogMergePolicy` have their own
  rules for reclaiming deletions, left at their defaults). The sequence of operations depends only on the seed and the mix, not on the engine.
- **Document text.** Original documents are `<corpus name> lcdoc`. An addition is `<corpus name> lcdoc freshdoc lcver zqv<k>x`. An update replaces the
  extension of the name with another one (`jpg`, `png`, `pdf`, `txt`, `docx` or `cs`, so a stale posting would show up in the extension queries) and
  appends ` lcver zqv<k>x`, where k is the number of the operation: every version of every document has a token of its own. `lcdoc` is a token every live
  document has; `lcver` marks documents that were added or updated; `freshdoc` the added ones.
- **Timing.** The timed region is one `Apply` call (the change, the refresh that makes it visible and one count query for the probe token); the layout read, the model update and
  all validation are outside it. The probe is the document's version token: exactly one hit after an addition or update, none after a deletion (a
  deletion of an untouched original document has no token of its own, its probe counts a token that no document has, so every operation type
  ends with the same one-term count query).
- **Layout over time.** After every call the engine's segment layout is read (untimed): number of segments, physical documents per segment, dead
  documents. Sharp: `IndexSnapshot.Segments` and `IndexSnapshot.Dead` (`DeadSet.Count`) through reflection; Lucene.NET: `MaxDoc` and
  `NumDeletedDocs` of the leaf readers; Tantivy: `IndexStats.Segments` (`DocumentCount + DeletedDocumentCount`, `DeletedDocumentCount`). The largest segment is
  identified by reference (Sharp), segment name (Lucene) or segment id (Tantivy); every call after which another segment is the largest is recorded as an
  event with the sizes before and after and the latency of that call and of its neighbours. A merge in the background becomes visible in the first call
  after it published, so for Sharp and Tantivy the latency attached to an event is not the merge duration (the report says so where it matters); for Lucene
  the merge scheduler hook gives the duration of every merge.
- **Merges, memory, reader.** As in the sustained benchmark: merge counts, documents written and time (Sharp's statistics through reflection, Lucene's scheduler hook,
  Tantivy as falls of the segment count), peak RSS sampled every 20 ms, managed allocations and GC pauses from the runtime. The optional paced reader
  (`--reader 1 --rate R`) cycles three queries 1 ms apart and tags each one with "a merge was running" and "overlapped a write call over 10 ms".
- **Schema change against the other reports.** To replace and delete by id, Lucene.NET indexes the id as a `StringField` (not analysed, not stored) next to
  the stored id, and Tantivy's `id` field is now indexed (it was stored and fast only). The base build of every scenario of this benchmark therefore indexes one more
  field in these two engines than in the earlier sustained report, which makes the base build slightly more expensive and the segments slightly larger. Sharp
  needs no change (ids are the keys of the index). The lifecycle documents also carry the extra token `lcdoc`.
- **Tantivy.** Same writer as in the sustained benchmark: a bulk writer for the build, then a 50 MB heap one-thread writer for the operations; merge policy default. One
  commit per visible change costs 4 to 5 ms even into a RAM index, so the Tantivy runs use fewer operations (the report states how many); its stream is a prefix of
  the others'.

## Validation

- **Model.** The harness keeps the expected index in flat arrays (version, swapped extension, live flag per id) and derives each document's text from them. After every call
  (untimed) the changed id is checked: after an addition or update the version token finds exactly that id and the superseded version token finds nothing; after a deletion
  a query that ANDs the document's own tokens loses exactly one hit and its version token finds nothing.
- **Periodic and final checks.** Every `--check-every` operations the live count (`lcdoc`), the added count (`freshdoc`) and a sample of latest, superseded and deleted version
  tokens are compared with the model. At the end the sorted id lists of `lcdoc` (every live document), `lcver`, `freshdoc`, `jpg`, `final`, `report AND pdf` and `rep*` are compared with
  the id sets the model computes from its own texts with an independent tokenizer, and 300 latest, up to 200 superseded and up to 200 deleted version tokens are probed again. A
  *mismatch* is a missing or an extra id in a set, or a token that finds the wrong id. The tables show the sums; all must be 0. The sha256 of every id list is stored, so
  runs of different engines that applied the same number of operations can be compared byte for byte.
- **Not checked.** Tokens of deleted original documents that were never updated: such a document has no token of its own, it is covered by the id sets (it must be absent from `lcdoc`
  and from every query set) and by the AND-count after its deletion. Ranking is not checked.

## Caveats

- One 4-vCPU VM, not pinned; the engines' background threads share the cores with the harness. Compare shapes and ratios, not absolute values from another machine.
- Merge policies are the engines' defaults and differ in how they treat deletions, so *when* the largest segment is rewritten differs between engines; the plots show when each one does it.
- The layout is read after every call, so a replacement of the largest segment is dated to the call after which it was first seen. Sharp's updater does not publish single merges, so a Sharp
  merge has no duration of its own, only a count and a total.
- The untimed work between calls (model update, probes, layout read) keeps the engines' caches and threads busier than a bare write loop would; it is the same for all three engines.
- Tantivy runs fewer operations (a prefix of the same stream), so its dead-document share of the largest segment is lower at the end than the others' unless a smaller n is used.
- A single writer thread; one run per cell.
