# Changelog

All notable changes to **SearchEngine.Sharp** are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.6.0] - 2026-11-24

Public API changes are additive only (reflection comparison against 0.5.6); the platform moves to .NET 11.

### Breaking (platform)

- Target framework `net10.0` → `net11.0` (C# 15). `Microsoft.Extensions.DependencyInjection` 10.0.11 → 11.0.0.
  Consumers on .NET 10 stay on 0.5.6; no source changes are needed when moving to .NET 11.

### Added

- `NameMatcher`: evaluates a query against one name with exactly the semantics of `Find` on an index that holds
  only that name (same `SearchTokenization`, operators, globs, regex, Unicode options). Use it for one-shot
  searches that filter names during a directory scan instead of building an index.
- `UnicodeMatching` flags and `SearchTokenization.WithUnicodeMatching(...)` (opt-in; default unchanged): NFC
  composition, case folding beyond per-character lowercase (`ß`→`ss`, `ς`→`σ`, `İ`→`i`, …), accent folding, and
  grapheme-cluster boundaries for `Within` and glob matches. See [docs/query-semantics.md](docs/query-semantics.md#unicode-matching).
  Composition and accent folding need Unicode normalization: in globalization-invariant mode `WithUnicodeMatching`
  throws `PlatformNotSupportedException` for `Compose` and `RemoveDiacritics`.
- `FacetValues.Create(params ReadOnlySpan<KeyValuePair<string, long>>)` — a facet bag without an intermediate
  dictionary. Duplicate names throw `ArgumentException`.
- `IngestPublishOptions.StrictMaxStaleness` (default `false`) — restores the 0.5.6 staleness behaviour (see Fixed).

### Fixed

- **Progressive ingestion at scale:** under `Adaptive`, once one full rebuild took longer than `MaxStaleness`
  (about 0.4–0.5 million file names on a laptop-class CPU), every batch was already stale when the previous
  rebuild finished and the publisher rebuilt back to back every `ChannelCapacity` (10k) entries. A 1M-file scan
  made 57–74 full rebuilds, allocated ~30 GB and took ~2 minutes on one core. A staleness-triggered publish now
  also waits `AdaptiveMultiplier` (`k`) × the last rebuild duration after that rebuild (13–16 rebuilds, ~20 s).
  Worst staleness is `max(MaxStaleness, (1 + k) × rebuild)` (1.2 s at a 400 ms rebuild instead of 1.0 s);
  unchanged while rebuilds are faster than `MaxStaleness / (1 + k)`.
- Progressive ingestion no longer leaves buffered entries unpublished while the scanner is idle: a timer that woke
  slightly before its publish was due, or a buffer below `MinTimerPublishBatch` that reached `MaxStaleness`, waited
  for the next scanner entry instead of publishing. The publisher now always re-arms a timer while entries are
  buffered.
- `ProgressiveIndexIngestion.IngestAsync` never completed when the publisher failed (an exception from the
  `IIndexUpdater`, a `Published` or `onPublished` handler, or `IProgress<T>`) while the scanner was blocked on a
  full channel. The scanner is now cancelled and disposed and the task faults with the original exception. A scan
  error that arrived while the publisher was waiting no longer skips the final flush of buffered entries, and a
  cancelled ingestion with `FlushOnCancellation` also publishes entries still queued in the channel (they counted as
  accepted but were dropped).
- Concurrent first `NaturalSortAscending` queries on a new snapshot each built their own copy of the sort
  permutation (8 at once at 1M names: 2.3 s each, 1.1 GB allocated); they now share one build (0.8 s, 161 MB).
- `Find`/`CountMatches` with `enableOperators: true` threw `ArgumentOutOfRangeException` for expressions starting
  with `NOT AND` or `NOT OR`, and returned nothing for misplaced operators elsewhere (`a NOT AND b`, `(AND a)`,
  `(a OR) b`, `a () b`). Malformed operator sequences are now corrected by the documented rules at the start and
  end of the expression and of every parenthesised group.
- Indexing compared raw tokens case-insensitively but stored them lowercased: a document whose tokens become equal
  only after lowercasing (`Straße STRAẞE`, `k` and the Kelvin sign) was posted twice, so `Find` returned its id
  twice and `CountMatches` counted it twice; the second of two tokens that are equal only under case-insensitive
  comparison (`ς σ`, `µ μ`, Greek symbol variants such as `β ϐ`) was never indexed. Tokens are now deduplicated on
  their lowercase form with ordinal comparison. `TextNormalizer.ForEachUniqueWord` requires an ordinal set and
  throws `ArgumentException` otherwise.
- `FacetValues.FromDictionary` enumerates the source once into a buffer, so a concurrently mutated dictionary can no
  longer produce a bag with trailing null names or an `IndexOutOfRangeException`, and it rejects null and duplicate
  names with `ArgumentException` exactly like `FacetValues.Create(span)`.
- `IndexUpdater` mutations are atomic: when building the new snapshot throws (for example for an invalid facet bag), the
  stored entries and the published snapshot stay as they were and later calls work. `Clear` and removing the last
  entry keep the tokenization and Unicode options of the updater instead of publishing a default-preset empty snapshot.

### Changed

- **Bigram index is lazy:** built on the first `WordMatchMethod.Within` query against a snapshot instead of in
  every `Build` (16–25% of build time; Exact, glob and regex never use it). The first `Within` query after a
  publish pays it (1M FileMask names: ~0.17 s once per snapshot, measured on a 20-core x64). Concurrent first `Within` queries share that one build
  and wait for it (8 at once at 1M names: 0.17 s each, 214 MB allocated in total).
- **Within:** a candidate word is searched with one vectorized `IndexOf` instead of comparing a sliding window at
  every position: 1.3–5× faster at 1M names on x64 (`within-3` 1.78 → 0.75 ms, one-character queries 12.7 → 3.4 ms).
- **Glob leaves** `abc*`, `*abc`, `*abc*`, `ab*cd` (no `?`) use vectorized span primitives: 4–6× faster for
  suffix/infix patterns on whole-file-name indexes (`*.pdf` over 1M names: 42 → 8 ms). Results unchanged.
- **Snapshot build allocations:** `FacetValues` stores arrays instead of a copied dictionary; facet values go
  straight into their columns when the entry count is known; an already-lowercase whole-text token reuses the
  caller's string. 1M file names with facets: build allocations 812 → 343 MB, peak RSS 1345 → 796 MB.

### Documentation

- `docs/file-search-guide.md`: *Index or plain scan?* with measured numbers; scan recipes use one
  `FileSystemEnumerable` pass, do not follow directory symlinks or junctions, keep id → path in a
  `ConcurrentDictionary`, and describe files that vanish during a scan.

### Tests

324 tests in total.

- Deterministic facet-ingestion and growth-aware publish-count tests (both were timing-dependent).
- Query operators: malformed operator sequences at the start, middle and end of expressions and groups (52 cases).
- Snapshot: lazy bigram index vs brute force with concurrent first callers; one build of the bigram index and of
  the sort permutation for 8 concurrent first callers.
- Glob matching: 50,000 random glob/word pairs (fast paths vs general matcher).
- Facets: columns with known and unknown entry count; `FacetValues` null, duplicate and concurrently mutated
  input (7).
- Ingestion: staleness back-off vs strict; timer and publisher-failure paths (19 in the class).
- `IndexUpdater`: atomic mutations and `Clear` keeping the configuration (13).
- Unicode matching (45 cases): lone surrogates, operators vs folded terms, grapheme boundaries for `Within` and
  globs, unfolded regexes, accent-stripping scope, invariant globalization.
- `TextNormalizer`: token de-duplication (15).
- `NameMatcher` vs index: 20 queries × 4 presets, and every combination of 20 texts × 43 queries × 3 methods × 8
  tokenizations, including empty text, custom separators and a regex timeout.

### Benchmarks

- `benchmarks/SearchEngine.Sharp.Profiling` (not part of the solution build): Commander-style file search harness,
  corpus generator, query-path, scale, persistent-index and concurrency modes, scripts.

## [0.5.6] - 2026-08-16

### Changed

- **Selectivity-aware query pipeline:** `ResultMaterializer` uses `FastBitSet.CopySetBitOrdinals()` for SnapshotOrder materialization; hybrid facet apply (sparse K → in-place on hits, dense K → full column scan); hybrid NaturalSort (K=0/1 fast paths, precomputed sort-K below 80% density, global permutation above). Thresholds in `SelectivityThresholds` (BDN-derived). `IngestPublishOptions.GrowthAwareBatchCap` is `true` by default for `Adaptive` policy. Batch cap grows to `max(FixedBatchSize, indexedDocumentCount)` after each publish, cutting 100k fast-scan rebuild amplification from ~**25×** to ~**2.2×** (~7 publishes instead of 50). Set `GrowthAwareBatchCap = false` to restore fixed 2k batch behaviour.
- **Exact + facet queries:** single-term exact queries with a `FacetFilter` now use a posting-span fast path — facet predicates are evaluated only for ordinals in the posting list, not all documents. `CountMatches` and filter-only queries received analogous optimisations.
- **Single-operand fast path with `enableOperators:true`:** expressions that tokenize to one word (no `AND`/`OR`/`NOT`/parentheses) now use posting-span fast paths for Exact and Exact+Facet, matching `enableOperators:false` behaviour.
- **Bigram index:** skip duplicate ordinal entries per bigram list (e.g. `banana` no longer registers twice under `an` / `na`).
- **`WithinBigramQueryMatcher` (benchmark assembly only):** A/B legacy first-bigram vs experimental rarest-bigram — rarest **~1.07× slower** on x64 (`tion`, file corpus); **not shipped**. Production `QueryMatcher` uses direct first-bigram path unchanged except ordinal dedupe in builder.
- **`NaturalSortBenchmark` / `ProgressiveNaturalSortBenchmark`:** `IterationSetup` yields true cold NaturalSort (see committed CSV). x64 @ 100k: cold **23.4 ms**, warm **77 μs**, progressive 7× cold **48.4 ms** vs SnapshotOrder **134.5 μs** (~360×) — [`artifacts/pass2-x64-win/`](audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/pass2-x64-win/README.md).
- Benchmark reports and audit evidence updated with post-fix measurements on x64 and ARM64.
- **MicroBenchmarks fingerprint CLI:** `--fingerprint` now accepts a directory or `.json` file path (fixes nested `environment-fingerprint.json/environment-fingerprint.json` output bug).
- **`docs/file-search-guide.md`:** updated publish count (~7), facet fast-path semantics, operator guidance, and NaturalSort cold/warm requery cost (x64 measured).

### Added

- `benchmarks/SearchEngine.Sharp.MicroBenchmarks` — BenchmarkDotNet project (`ExactFacetBenchmark`, `OperatorsOnBenchmark`, `FileSearchBenchmark`, `NaturalSortBenchmark`, `ProgressiveNaturalSortBenchmark`, `WithinBigramBenchmark`, `BitSetMaterializationBenchmark`, `NaturalSortSelectivityBenchmark`, `FacetSelectivityBenchmark`, `SelectivityPipelineBenchmark`, `FileMaskGlobBenchmark`, `SnapshotBuildAllocationBenchmark`, `MemoryDiagnoser`, environment fingerprint CLI, CSV/Markdown/HTML export).
- `FastBitSet.CopySetBitOrdinals()` / `ForEachSetBit()` — set-bit enumeration primitive; used by selectivity-aware `ResultMaterializer`.
- `NaturalSortCrossoverBenchmark` — empirical sort-K vs global permutation crossover (precomputed keys).
- `SelectivityPipelineCounts` — records measured `textHitCount` / `postFacetHitCount` for E2E benchmarks.
- `FileSearchDataFactory` — realistic file-name corpus for product-shaped benchmarks.
- BDN CSV evidence under `audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/` (pass 1 ExactFacet per platform; pass 2 on x64 + ARM64; pass 3 selectivity on x64 + ARM64 including post-implementation SelectivityPipeline CSV).
- `benchmarks/SearchEngine.CompetitorBenchmarks/` — library benchmark harness (SearchEngine.Sharp version comparison + Tantivy 0.22, MiniSearch 7, Bleve 2.4 adapters with verified hit counts). CSV evidence under `audits/.../artifacts/competitor-benchmarks/`.
- [`docs/0.5.6-selectivity-research.md`](docs/0.5.6-selectivity-research.md) — pass 3 research closure and 0.5.6 implementation scope.

### Tests

324 tests in total.

- `Ingest_GrowthAwareBatchCap_ReducesPublishCountAtScale`
- `ExactSingleTerm_WithFacetFilter_MatchesWithinPlusFilter`
- `ExactSingleTerm_WithFacetFilter_EnableOperatorsTrue_SameAsOff`
- `SingleSemanticWordTests`
- `BigramIndexTests` — invariant that each bigram list contains a word ordinal at most once (`banana` → `an` / `na`)
- `SelectivityPipelineTests`
- `SelectivityThresholdsTests`

## [0.5.5] - 2026-07-03

### Changed

- **Breaking:** `WordMatchMethod.Regex` now uses standard unanchored `Regex.IsMatch` on indexed tokens (pattern compiled as-is). In 0.5.4 the engine wrapped patterns as `^(?:pattern)$`; use explicit `^...$` when you need a full-token match.
- Constructs unsupported by `NonBacktracking` (lookarounds, backreferences) fall back to the default backtracking engine with the same `IgnoreCase | CultureInvariant` options and 1 s timeout instead of returning an empty result.
- A `RegexMatchTimeoutException` during the token scan (catastrophic backtracking on the fallback path) is caught and returns an empty result instead of propagating to the caller.

## [0.5.4] - 2026-07-03

### Added

- `WordMatchMethod.Regex` — the entire expression is one .NET regular expression matched against whole indexed tokens (anchored `^(?:pattern)$`, `IgnoreCase | CultureInvariant | NonBacktracking`, 1 s match timeout). Boolean parsing and query separators are bypassed.
- LRU cache of compiled regex patterns (8 entries) for type-ahead workloads.
- Invalid patterns and constructs unsupported by `NonBacktracking` (lookarounds, backreferences) return an empty result instead of throwing.
- Documentation: regex semantics in `docs/query-semantics.md`, README, and `docs/api.md`.

## [0.5.3] - 2026-07-01

### Added

- `SearchTokenization` presets (`Default`, `FileMask`, `Create`) stored per snapshot; `IndexUpdater` and `IndexSnapshotBuilder` overloads; DI `AddSearchEngine(SearchTokenization)`.
- `FileMask` preset: whole-name search semantics — bare terms match the entire name, `*.pdf` is end-anchored, query terms split on whitespace only.

## [0.5.2] - 2026-07-01

### Added

- Glob leaf matching: query tokens with `*` or `?` are auto-routed to whole-token glob matching inside boolean expressions.
- Facet columns on `IndexedEntry` with post-query `FacetFilter` (range and bitmask predicates, AND-combined).
- `ISearchEngine` overloads for `Find` and `CountMatches` with optional `FacetFilter`.
- Documentation: query semantics reference (`docs/query-semantics.md`), file search guide (`docs/file-search-guide.md`), public API reference (`docs/api.md`).

## [0.5.1] - 2026-07-01

### Added

- `ProgressiveIndexIngestion` — batched progressive indexing during long file scans without per-entry O(N²) rebuild cost.
- `IngestPublishPolicy` and `IngestPublishOptions` — fixed batch, time debounce, and adaptive publish policies (default: adaptive with 2k batch, 1 s staleness cap).
- `SyntheticPathFeed` — synthetic tokenized paths for tests, benchmarks, and demos.
- Ingestion policy comparison benchmark (`--ingestion-policy` in `SearchEngine.Sharp.Benchmarks`).
- `demos/ProgressiveIngestion.Demo` — console demo showing match counts growing during a scan.
- `docs/ingestion-policy-report.md` — measured policy comparison at 100k scale.
- README section **Live indexing during scans**.

### Tests

324 tests in total.

- Progressive ingestion functional, cancellation, and read-while-write stress tests.

## [0.5.0] - 2026-06-24

### Added

- Initial release: in-memory inverted index with exact/within token search, boolean operators, natural sort, snapshot-based concurrent reads, and DI registration.

[0.6.0]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.6...v0.6.0
[0.5.6]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.5...v0.5.6
[0.5.5]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.4...v0.5.5
[0.5.3]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/buchmiet/SearchEngine.Sharp/releases/tag/v0.5.0
