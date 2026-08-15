# Changelog

All notable changes to **SearchEngine.Sharp** are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- **Progressive ingestion:** `IngestPublishOptions.GrowthAwareBatchCap` is `true` by default for `Adaptive` policy. Batch cap grows to `max(FixedBatchSize, indexedDocumentCount)` after each publish, cutting 100k fast-scan rebuild amplification from ~**25×** to ~**2.2×** (~7 publishes instead of 50). Set `GrowthAwareBatchCap = false` to restore fixed 2k batch behaviour.
- **Exact + facet queries:** single-term exact queries with a `FacetFilter` now use a posting-span fast path — facet predicates are evaluated only for ordinals in the posting list, not all documents. `CountMatches` and filter-only queries received analogous optimisations.
- **Single-operand fast path with `enableOperators:true`:** expressions that tokenize to one word (no `AND`/`OR`/`NOT`/parentheses) now use posting-span fast paths for Exact and Exact+Facet, matching `enableOperators:false` behaviour.
- **Bigram index:** skip duplicate ordinal entries per bigram list (e.g. `banana` no longer registers twice under `an` / `na`).
- **`WithinBigramBenchmark`:** A/B first-bigram vs rarest-bigram on file corpus (`tion` @ 100k) — rarest ~1.12× slower on x64; production keeps first-bigram selection.
- Benchmark reports and audit evidence updated with post-fix measurements on x64 and ARM64.
- **MicroBenchmarks fingerprint CLI:** `--fingerprint` now accepts a directory or `.json` file path (fixes nested `environment-fingerprint.json/environment-fingerprint.json` output bug).
- **`docs/file-search-guide.md`:** updated publish count (~7), facet fast-path semantics, and operator guidance.

### Added

- `benchmarks/SearchEngine.Sharp.MicroBenchmarks` — BenchmarkDotNet project (`ExactFacetBenchmark`, `OperatorsOnBenchmark`, `FileSearchBenchmark`, `NaturalSortBenchmark`, `ProgressiveNaturalSortBenchmark`, `WithinBigramBenchmark`, `MemoryDiagnoser`, environment fingerprint CLI, CSV/Markdown/HTML export).
- `FileSearchDataFactory` — realistic file-name corpus for product-shaped benchmarks.
- BDN CSV evidence under `audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/` (x64 Windows, macOS ARM64, Ubuntu ARM64). Full QA evidence bundle (CPU SKU, SDK, PGO, CI matrix) remains follow-up work — see audit report F-03.

### Tests

- `Ingest_GrowthAwareBatchCap_ReducesPublishCountAtScale`
- `ExactSingleTerm_WithFacetFilter_MatchesWithinPlusFilter`
- `ExactSingleTerm_WithFacetFilter_EnableOperatorsTrue_SameAsOff`
- `SingleSemanticWordTests`

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

- Progressive ingestion functional, cancellation, and read-while-write stress tests.

## [0.5.0] - 2026-06-24

### Added

- Initial release: in-memory inverted index with exact/within token search, boolean operators, natural sort, snapshot-based concurrent reads, and DI registration.

[0.5.5]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.4...v0.5.5
[0.5.3]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/buchmiet/SearchEngine.Sharp/releases/tag/v0.5.0
