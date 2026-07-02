# Glob matching and facet filters report

Measured on the development machine (X64, 12 logical cores, AVX2, .NET 10.0.9).

Harness:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --warmup 2 --iterations 5
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet --warmup 2 --iterations 5
```

Synthetic data: 32 tokens per document, 500 queries per scenario, seed 1337. Facet scenarios attach `size` (bytes) and `modified` (UTC ticks) columns; filters use `size` in 1 KiB–1 MiB and `modified` within the last 30 days.

## Design decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Facet storage on entries | Optional `FacetValues?` on `IndexedEntry` | Least invasive; all updater paths already carry entries |
| Facet key type | `string` resolved once per query via snapshot dict | Simple caller model; throw on unknown |
| Missing facet value | `0` in column | Deterministic; zero overhead when no facets registered |
| Explicit `WordMatchMethod.Glob` | **Not added** | Auto-routing when `*`/`?` present |
| Glob bigram pruning | **Deferred** | Linear word scan first; benchmark before adding |
| Filter scan strategy | Single combined ordinal pass | One temp bitset vs per-predicate intersect chain |
| Filter application point | After full boolean eval (incl. NOT) | Evaluator untouched; filter is post-processing |

## Query throughput — text only (after glob, before facet filter)

| Scenario | Exact q/s | Within q/s | Glob q/s | Boolean q/s |
|----------|----------:|-----------:|---------:|------------:|
| small (10k) | 321,370 | 21,837 | 16,743 | 60,061 |
| medium (100k) | 144,455 | 3,748 | 5,296 | 17,662 |
| large (250k) | 300,810 | 1,377 | 3,452 | 6,296 |

Glob is competitive with Within on medium/large in this synthetic mix (prefix/suffix/`?` patterns on vocabulary words). Exact fast-path is unchanged for non-metacharacter tokens.

## Query throughput — with facet columns (`--facet`)

| Scenario | Exact+Filter | Within+Filter | Glob+Filter | Filter-only |
|----------|-------------:|--------------:|------------:|------------:|
| medium (100k) | 1,748 q/s | 1,458 q/s | 1,262 q/s | 975 q/s |
| large (250k) | 694 q/s | 586 q/s | 567 q/s | 398 q/s |

Facet filtering adds a full ordinal scan (single pass over all predicates per document) plus a bitset intersect. Cost dominates on large indexes; filter-only is slowest because every document is considered before materialization.

### Relative overhead (medium, P50 latency)

| Mode | P50 (ms) | vs text-only P50 |
|------|----------|------------------|
| Exact | 0.0044 | baseline |
| Exact+Filter | 0.5049 | ~115× |
| Within | 0.1558 | — |
| Within+Filter | 0.6899 | ~4.4× |
| Glob | 0.1185 | — |
| Glob+Filter | 0.7900 | ~6.7× |

Exact+Filter loses the single-word posting fast path because the filter overload always runs `ExecuteQuery`. Within/Glob+Filter overhead is a modest multiple of the text query alone at 100k scale.

## Semantics recap

- **Glob:** whole-token anchored match; `*` alone matches all documents; `*`/`?` in a token bypass exact posting fast path.
- **Facets:** caller-encoded `long` values; dates → `DateTime.Ticks`, sizes → bytes, attributes → bitmasks.
- **Filter-only:** empty/whitespace expression with a non-empty `FacetFilter` returns all documents matching the filter.

## Follow-ups (not implemented)

- Bigram pruning for `MatchGlob` if profiling shows word scan as bottleneck.
- Optional fast path for filter-only queries that skips expression evaluation entirely (already uses all-true bitset; could skip renting when no text eval needed).
