# Changelog

All notable changes to **SearchEngine.Sharp** are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[0.5.2]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/buchmiet/SearchEngine.Sharp/releases/tag/v0.5.0
