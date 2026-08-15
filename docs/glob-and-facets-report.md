# Glob matching and facet filters report

Synthetic data: 32 tokens per document, 500 queries per scenario, seed 1337. Facet scenarios attach `size` (bytes) and `modified` (UTC ticks) columns; filters use `size` in 1 KiB–1 MiB and `modified` within the last 30 days.

Harness:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --warmup 2 --iterations 5
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet --warmup 2 --iterations 5
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*ExactFacet*'
```

## Design decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Facet storage on entries | Optional `FacetValues?` on `IndexedEntry` | Least invasive; all updater paths already carry entries |
| Facet key type | `string` resolved once per query via snapshot dict | Simple caller model; throw on unknown |
| Missing facet value | `0` in column | Deterministic; zero overhead when no facets registered |
| Explicit `WordMatchMethod.Glob` | **Not added** | Auto-routing when `*`/`?` present |
| Glob bigram pruning | **Deferred** | Linear word scan first; benchmark before adding |
| Exact + facet (single term) | **Posting-span fast path** (since 0.5.6) | Check facet predicates only for ordinals in the posting list |
| Filter-only | **Direct facet bitset** (since 0.5.6) | Skip `RentAllTrueBitSet` + redundant intersect |
| Filter application (general) | After full boolean eval (incl. NOT) | Evaluator untouched; filter is post-processing |

---

## Post-fix (v0.5.6+) — workload harness

Measured 2026-08-15, Windows x64 (.NET 10.0.11), `--facet --warmup 2 --iterations 5 --seed 1337`.

### Relative overhead — medium 100k (P50 latency)

| Mode | P50 (ms) | vs Exact baseline |
|------|----------|-------------------|
| Exact | 0.0044 | baseline |
| **Exact+Filter** | **0.0161** | **~3.7×** (was ~115×) |
| Within+Filter | (see full run) | modest multiple of Within |
| Filter-only | 0.2480 | O(N) facet scan remains |

Exact (no filter) unchanged. Exact+Filter now uses `TryFindExactWithFacetFastPath()` — facet work is **O(hits × predicates)**, not O(N).

### Query throughput — with facet columns (medium/large)

| Scenario | Exact+Filter q/s | Filter-only q/s |
|----------|-----------------:|----------------:|
| medium (100k) | **63,858** | 3,524 |
| large (250k) | **94,436** | 1,369 |

---

## Post-fix (v0.5.6+) — BenchmarkDotNet `ExactFacetBenchmark`

100k documents, seed 1337, `InProcessNoEmitToolchain`. Raw CSV: [`audits/.../artifacts/`](../audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/).

| Platform | ExactOnly | ExactWithFacetFilter | BDN Ratio | Alloc (facet) |
|----------|----------:|---------------------:|----------:|--------------:|
| Windows x64 | 1.150 μs | 1.683 μs | **1.46×** | 6.02 KB |
| macOS ARM64 | 2.018 μs | 2.806 μs | **1.39×** | 6.02 KB |
| Ubuntu ARM64 | 2.174 μs | 3.019 μs | **1.39×** | 6.02 KB |

Audit gate: Exact+Filter workload P50 **< 0.10 ms** — **met** (0.016 ms x64). Exact regression ≤ 5% — **met**.

---

## Historical baseline — pre-0.5.6 (v0.5.5)

Measured on development machine (X64, 12 logical cores, AVX2, .NET 10.0.9).

### Query throughput — text only

| Scenario | Exact q/s | Within q/s | Glob q/s | Boolean q/s |
|----------|----------:|-----------:|---------:|------------:|
| medium (100k) | 144,455 | 3,748 | 5,296 | 17,662 |
| large (250k) | 300,810 | 1,377 | 3,452 | 6,296 |

### Relative overhead — medium 100k (P50)

| Mode | P50 (ms) | vs text-only P50 |
|------|----------|------------------|
| Exact | 0.0044 | baseline |
| Exact+Filter | **0.5049** | **~115×** |
| Within+Filter | 0.6899 | ~4.4× |

Exact+Filter previously lost the posting fast path and scanned all ordinals for facet predicates.

---

## Semantics recap

- **Glob:** whole-token anchored match; `*` alone matches all documents; `*`/`?` in a token bypass exact posting fast path.
- **Facets:** caller-encoded `long` values; dates → `DateTime.Ticks`, sizes → bytes, attributes → bitmasks.
- **Filter-only:** empty/whitespace expression with a non-empty `FacetFilter` returns all documents matching the filter.

## Follow-ups

- Bigram pruning for `MatchGlob` if profiling shows word scan as bottleneck.
- BDN coverage for `FastBitSet`, general facet evaluator on non-exact paths, allocation rate (`C-01`).
