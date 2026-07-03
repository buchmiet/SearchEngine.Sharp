# Changelog

All notable changes to **SearchEngine.Sharp** are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[0.5.3]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/buchmiet/SearchEngine.Sharp/releases/tag/v0.5.0
