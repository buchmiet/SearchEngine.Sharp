# Pass 3 benchmark artifacts (x64 Windows)

Measured 2026-08-15. Source of truth: the `*-report.csv` files in this folder.

**Thesis under test:** the query pipeline is **selectivity-blind** — several stages scan or sort all **N** documents even when the text query yields **K ≪ N** hits.

Harness (`SearchEngine.Sharp.MicroBenchmarks`, `InProcessNoEmitToolchain`, `WarmupCount=1`, `IterationCount=3`):

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*BitSetMaterializationBenchmark*"
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*NaturalSortSelectivityBenchmark*"
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*FacetSelectivityBenchmark*"
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*SelectivityPipelineBenchmark*"
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*FileMaskGlobBenchmark*"
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter "*SnapshotBuildAllocationBenchmark*"
```

Environment: Windows x64, 12 cores, AVX2, .NET 10.0.11. File corpus: `FileSearchDataFactory` @ 100k (250k where noted), seed 2026.

Prototypes live in `SelectivityProbe.cs` (benchmark assembly only) — **not shipped**.

---

## 1. BitSet materialization (`BitSetMaterializationBenchmark-report.csv`)

N=100k. Synthetic bitsets with K hits spread across ordinals.

| K | Current O(N) scan | Enumerate set bits | Speedup |
|--:|------------------:|-------------------:|--------:|
| 0 | 36.3 μs | **404 ns** | **~90×** |
| 1 | 40.5 μs | **395 ns** | **~103×** |
| 10 | 39.8 μs | **489 ns** | **~81×** |
| 100 | 40.2 μs | **449 ns** | **~90×** |
| 1,000 | 36.9 μs | **1.37 μs** | **~27×** |
| 10,000 | 36.2 μs | **9.68 μs** | **~3.7×** |
| 50,000 | 44.6 μs | 46.4 μs | ~1.0× |
| 100,000 | 76.6 μs | 90.0 μs | ~0.9× |

**Finding:** `SnapshotOrder` materialization scans all N `Get()` calls regardless of K. `FastBitSet.CopySetBitOrdinals()` (new primitive) is flat ~O(N/64 + K). Crossover near **50% density** — at 100% both paths are O(N).

---

## 2. NaturalSort selectivity (`NaturalSortSelectivityBenchmark-report.csv`)

Cold snapshot each iteration (`IterationSetup`, `InvocationCount=1`). Compares current `GetSortedPermutation()` + full permutation scan vs prototype **enumerate K + sort K keys only**.

| K | Current (full N sort + scan) | Sort K only | Speedup |
|--:|-----------------------------:|------------:|--------:|
| 0 | **22–63 ms** (cold build dominates) | **161 μs** | **~140×** |
| 1 | **22.4 ms** | **216 μs** | **~104×** |
| 10 | **22.2 ms** | **37.6 μs** | **~590×** |
| 100 | **22.0 ms** | **134 μs** | **~165×** |
| 1,000 | **22.6 ms** | **1.66 ms** | **~14×** |
| 10,000 | **21.8 ms** | 24.0 ms | ~0.9× (crossover) |
| 50,000 | **22.0 ms** | 106 ms | ~0.2× |
| 100,000 | **21.9 ms** | 193 ms | ~0.1× |

**Finding:** cold NaturalSort cost is **independent of K** in the current pipeline — 0 hits pays the same ~22 ms as 1 hit. Sort-K-only wins for **K ≲ 1–5% of N** (~1k @ 100k). Above ~10% use cached global permutation.

**Recommended hybrid:** enumerate K → if K==0 return []; if K==1 return; if K small sort K; else `GetSortedPermutation()` + filter.

---

## 3. Facet on text hits (`FacetSelectivityBenchmark-report.csv`)

Range facet `size ∈ [1KiB, 1MiB]`. Current `FacetFilterEvaluator.Apply()` scans all N ordinals.

| K text hits | Current O(N) facet | Facet on K hits only | Speedup |
|------------:|-------------------:|---------------------:|--------:|
| 0 | 62.4 μs | **865 ns** | **~72×** |
| 1 | 63.8 μs | **1.07 μs** | **~60×** |
| 10 | 64.0 μs | **1.13 μs** | **~57×** |
| 100 | 63.9 μs | **1.38 μs** | **~46×** |
| 1,000 | 64.4 μs | **3.89 μs** | **~17×** |
| 10,000 | 62.9 μs | 29.4 μs | ~2.1× |
| 50,000 | 62.8 μs | 141 μs | ~0.4× |
| 100,000 | 64.8 μs | 283 μs | ~0.2× |

**Finding:** confirms F-02 "part two" — Within/Glob/Boolean+Facet still use full O(N) `Apply()`. Facet-on-K wins decisively for sparse results (typical typeahead). Full scan remains cheaper only when K approaches N (facet scan itself is only ~63 μs @ 100k).

Historical signal: Within ~0.156 ms + facet adds ~0.53 ms floor = same full-scan mechanism.

---

## 4. End-to-end pipeline (`SelectivityPipelineBenchmark-report.csv`)

Real `SearchEngineSharp.Find`, cold snapshot per iteration, file corpus @ 100k.

| Scenario | Mean | Notes |
|----------|-----:|-------|
| **0 hits + NaturalSort** | **55.4 ms** | query `zzzznotfound999`; still builds full natural permutation |
| **~20 hits Within+Facet+NaturalSort** | **21.9 ms** | query `report`; dominated by cold sort, not facet |
| **~20 hits Within+Facet SnapshotOrder** | **199 μs** | same query; **~110× faster** without NaturalSort |

**Finding:** E2 confirmation — zero-hit query with NaturalSort pays full cold-sort cost. Selective typeahead should not trigger global N-sort.

---

## 5. FileMask glob benchmark (`FileMaskGlobBenchmark-report.csv`)

| Corpus | Default `*.pdf` | FileMask `*.pdf` | Ratio |
|-------:|----------------:|-----------------:|------:|
| 100k | 44.0 μs (wrong: `.` splits token) | **260 μs** (whole filename) | **5.9×** |
| 250k | 112.6 μs | **333 μs** | **3.0×** |

FileMask `*.pdf` + facet @ 100k: **486 μs** (full O(N) facet scan on glob hits path).

**Finding:** `FileSearchBenchmark` with Default tokenization **does not measure** real `*.pdf` FileMask glob. Use `SearchTokenization.FileMask` for product-shaped glob benchmarks.

---

## 6. Snapshot build allocation (`SnapshotBuildAllocationBenchmark-report.csv`)

| Benchmark | Mean | Allocated |
|-----------|-----:|----------:|
| Full rebuild 100k (current) | **40.6 ms** | **48.7 MB** |
| Full rebuild 250k (current) | **88.3 ms** | **108.6 MB** |
| Token loop — CreateNormalizedWord then pool | **7.37 ms** | **10.1 MB** |
| Token loop — span lookup, allocate if new | **4.65 ms** | **3.0 MB** |

**Finding:** E1 allocation signal — `CreateNormalizedWord()` runs before `WordStringPool` dedupe; span-aware pool prototype cuts token-loop time **~0.63×** and allocation **~0.30×**. Full rebuild win TBD (needs integrated builder change).

---

## Architectural conclusion

| Stage | Current | Selectivity-aware target |
|-------|---------|--------------------------|
| Materialize | O(N) `Get()` scan | O(N/64 + K) enumerate |
| Facet (non-Exact) | O(N) `Apply()` | O(K) on text hits |
| NaturalSort | O(N) cold build always | K==0 skip; K small sort K; K large cached permutation |

**Priority for 0.5.6:** unified selectivity-aware query pipeline (not isolated NaturalSortKeyBuilder tuning).

ARM64 pass 3 not yet measured.
