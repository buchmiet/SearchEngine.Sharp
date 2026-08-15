# .NET Performance Review — SearchEngine.Sharp (ARM64/x64)

**Baseline target:** `SearchEngine.Sharp` @ `21dd346` (v0.5.5, .NET 10)  
**Remediated target (pass 1):** @ `32f8b7a` — commits `0d0efb3` (F-01/F-02), `32f8b7a` (F-03 harness)  
**Pass 2 target (unreleased):** @ `0d34a07` — operators-on fast path, bigram dedupe, product BDN; see [Pass 2 verification](#pass-2-verification-unreleased-034a07)  
**Pass 3 target (investigation):** selectivity-aware pipeline BDN — see [Pass 3 verification](#pass-3-verification-selectivity-aware-pipeline)  
**Audit package:** `dotnet-performance` (`buchmiet/audits`, branch `markdown-first-retrieval`)  
**Date:** 2026-08-15 (baseline + post-fix verification same day)  
**Phase 0:** first committed report; **Phase 1:** remediation verified on x64 + ARM64

Methodology gates applied per [`evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md) and [`severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md): E0 static signals are investigation candidates only; performance findings require E2+ with architecture-specific baselines.

---

## Remediation summary

| ID | Baseline | Post-fix (v0.5.6) | Gate | Status |
|----|----------|-------------------|------|--------|
| **F-01** | adaptive ~**25×** rebuild amplification (50 publishes @ 100k) | **~2.2×** (7 publishes) | < 10× (target < 5×) | **resolved** |
| **F-02** | Exact+Filter P50 **~0.53 ms** (~115× vs Exact) | **~0.016 ms** x64 (~3.7× vs Exact) | P50 < 0.10 ms | **resolved** |
| **F-03** | Stopwatch-only harness | BDN project + committed CSV artifacts + partial environment fingerprint | full QA bundle (CPU SKU, SDK, PGO, CI matrix) | **substantially remediated** (manual gate) |

Raw BDN CSV: [`artifacts/`](artifacts/README.md). Updated product docs: [`docs/ingestion-policy-report.md`](../../docs/ingestion-policy-report.md), [`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md).

---

## Post-fix verification — before → after

Measured 2026-08-15 after `32f8b7a`. Flags: `--seed 1337`, `--warmup 2 --iterations 5` (queries), `--ingestion-count 100000` (ingestion).

### F-01 — progressive ingestion (100k, fast scan)

| Policy | Baseline overhead× | Post-fix overhead× | Publishes (before → after) |
|--------|-------------------:|-------------------:|---------------------------:|
| adaptive | 23.6–25.1× (x64 + ARM64) | **2.13–2.22×** | 50 → **7** |
| fixed-2k | ~25–28× | ~28–29× (unchanged) | 50 |
| debounce-100ms | ~1.7–2.0× | ~1.7× | 2 |

**Mechanism:** `IngestPublishOptions.GrowthAwareBatchCap = true` (default). After each publish, batch cap becomes `max(FixedBatchSize, indexedDocumentCount)` → snapshot series **2k → 4k → 8k → 16k → 32k → 64k → 100k**. Theoretical amplification `(2+4+8+16+32+64+100)/100 = 2.26×`.

| Platform | adaptive overhead× | Publishes | Worst staleness |
|----------|-------------------:|----------:|----------------:|
| Windows x64 | 2.22 | 7 | 158 ms |
| macOS ARM64 | 2.21 | 7 | 179 ms |
| Ubuntu ARM64 | 2.13 | 7 | 204 ms |

All staleness values **≤ 1 s** gate.

### F-02 — exact + facet (medium 100k, workload P50)

| Scenario | Baseline P50 (x64) | Post-fix P50 (x64) | Post-fix P50 (ARM64) |
|----------|-------------------:|-------------------:|---------------------:|
| Exact | 0.0044 ms | 0.0044 ms (no regression) | 0.0027 ms |
| Exact + Filter | **0.5313 ms** | **0.0161 ms** | **0.0078 ms** |
| Ratio vs Exact | ~121× | **~3.7×** | ~2.9× |

**Mechanism:** `TryFindExactWithFacetFastPath()` / `TryGetExactFacetCount()` evaluate facet predicates only for ordinals in the exact posting span; filter-only skips `RentAllTrueBitSet`.

### F-02 — BenchmarkDotNet `ExactFacetBenchmark` (100k, seed 1337)

| Platform | ExactOnly | ExactWithFacetFilter | BDN ratio | Alloc (facet) |
|----------|----------:|---------------------:|----------:|--------------:|
| Windows x64 | 1.150 μs | 1.683 μs | **1.46×** | 6.02 KB |
| macOS ARM64 | 2.018 μs | 2.806 μs | **1.39×** | 6.02 KB |
| Ubuntu ARM64 | 2.174 μs | 3.019 μs | **1.39×** | 6.02 KB |

Filter-only remains O(N) at **~252 μs** (x64 BDN) — expected; not in F-02 scope.

### F-03 — benchmark infrastructure

- New project: `benchmarks/SearchEngine.Sharp.MicroBenchmarks/` — `MemoryDiagnoser`, CSV/Markdown/HTML export, `EnvironmentFingerprint`, `InProcessNoEmitToolchain` (sibling-repo layout fix in `32f8b7a`).
- Existing `SearchEngine.Sharp.Benchmarks` retained as workload runner (ingestion policy, facet scenarios).
- **Committed evidence:** BDN CSV exports per platform under [`artifacts/`](artifacts/README.md) (previously blocked by root `.gitignore` `artifacts/` rule — fixed in this branch).
- **Not yet complete vs original F-03 gate:** no automated CI matrix; PGO/tiering flags not recorded; `EnvironmentFingerprint` captures runtime/RID/arch/core count/ISA but not CPU SKU or SDK version.
- **Pass 2 extension:** BDN also covers operators-on, WithinBigram A/B, cold NaturalSort, and progressive requery — see [`artifacts/pass2-x64-win/`](artifacts/pass2-x64-win/README.md) (x64 CSV committed; ARM64 pass 2 not yet run).

---

## Pass 2 verification (unreleased @ `0d34a07`)

Measured 2026-08-15. Raw CSV: [`artifacts/pass2-x64-win/`](artifacts/pass2-x64-win/README.md). Numbers below match committed BDN exports.

### Operators-on fast path

`TryGetSingleSemanticWord()` reuses tokenizer + `CorrectTokens`; boolean/NOT/parentheses excluded; glob rejected via `ContainsMetacharacters`.

| Benchmark (x64) | Mean | vs baseline |
|-----------------|-----:|------------:|
| Exact operators off | 1,387 ns | 1.00 |
| Exact operators on | 1,397 ns | **1.01** |
| Exact+Facet off / on | 2,881 / 2,891 ns | **2.08×** vs Exact off |

**Status:** verified — no material Find regression with `enableOperators:true` for single-token exact paths.

### Bigram ordinal dedupe + rarest-bigram experiment

- **Shipped:** builder dedupe `list[^1] != i` (e.g. `banana` → one entry per bigram list for that word ordinal).
- **Rejected:** rarest-bigram selection — **7.40 μs** vs **7.93 μs** (**1.07×** slower) on file corpus query `tion`; experiment lives in `WithinBigramQueryMatcher` (benchmark assembly only).

### NaturalSort cold build (C-03 follow-up)

BDN uses `IterationSetup` → fresh snapshot, `InvocationCount=1` (true cold invocation).

| Scenario (x64, file corpus 100k) | Mean |
|----------------------------------|-----:|
| Cold first `NaturalSortAscending` | **23,426 μs** |
| Warm (same snapshot) | **77.3 μs** |
| SnapshotOrder baseline | **48.3 μs** |
| Progressive 7× cold NaturalSort requery | **48,389 μs** |
| Progressive 7× SnapshotOrder requery | **134.5 μs** (~**360×** less) |

**Assessment:** cold NaturalSort is a dominant cost in progressive file-search requery on x64 @ 100k. Not elevated to P2 (no production call-volume/SLO evidence), but no longer E0 — **E2 x64, medium confidence, follow-up**. ARM64 pass 2 unverified.

---

## Pass 3 verification (selectivity-aware pipeline)

Measured 2026-08-15. Raw CSV: [`artifacts/pass3-x64-win/`](artifacts/pass3-x64-win/README.md). Prototypes in `SelectivityProbe.cs` (benchmark assembly only).

### Core finding

The ~23 ms NaturalSort cost is a **symptom** of a broader pattern: multiple pipeline stages are **selectivity-blind** — they process all **N** documents even when the text query yields **K ≪ N** hits.

### NaturalSort vs K (cold snapshot, x64 @ 100k, precomputed keys)

| K / N | Global permutation | Sort K (precomputed) | Ratio |
|------:|-------------------:|---------------------:|------:|
| 0 | ~22 ms | **164 μs** | **~140×** |
| 10 | **21.8 ms** | **30 μs** | **~730×** |
| 1,000 | **22.3 ms** | **248 μs** | **~90×** |
| 10,000 | **21.7 ms** | **2.5 ms** | **~8.7×** |
| 100,000 | **22.9 ms** | **19.6 ms** | **~0.85×** (crossover ~90% density) |

Naive comparator prototype (rebuilds keys in sort) **invalidated** — gave false crossover ~10k. See `NaturalSortCrossoverBenchmark-report.csv`.

Zero-hit cold NaturalSort confirmed; stable component timing **~22 ms** (E2E 0-hit ~31 ms, high variance).

### Facet Apply vs K (x64 @ 100k)

General `FacetFilterEvaluator.Apply()` still O(N). Facet-on-K in-place prototype:

| K | Current | On K only | Speedup |
|--:|--------:|----------:|--------:|
| 10 | 64 μs | **1.1 μs** | **~57×** |
| 1,000 | 64 μs | **3.9 μs** | **~17×** |

E2E Within `"report"`: **textHitCount=10,000**, **postFacetHitCount=4**. SnapshotOrder **185 μs** vs NaturalSort **24.9 ms** (~134×).

### BitSet materialization (x64 @ 100k)

O(N) `Get()` scan flat **~37–77 μs** for K=0..10k. `CopySetBitOrdinals()`: **400 ns – 10 μs** at sparse K.

### Build allocation (x64)

Rebuild 100k: **40.6 ms**, **48.7 MB**. Span-aware word pool prototype on token loop: **4.65 ms / 3.0 MB** vs current **7.37 ms / 10.1 MB** — E1 signal for integrated builder change.

### FileMask glob benchmark gap

Default tokenization `*.pdf` @ 100k: **44 μs** (misleading — `.` splits query). FileMask whole-filename: **260 μs** (**5.9×**). `FileSearchBenchmark` must use `SearchTokenization.FileMask` for glob scenarios.

**Recommended 0.5.6 direction:** unified selectivity-aware query pipeline — see [`docs/0.5.6-selectivity-research.md`](../../docs/0.5.6-selectivity-research.md).

---

## Scope and limitations

- **In scope:** query hot paths, progressive ingestion rebuild cost, facet filtering, benchmark harness maturity, SIMD dispatch posture.
- **Out of scope (this run):** production telemetry (E4), disassembly review, peak RSS during index build, allocation-rate gates for general query paths (C-01).
- **Harness:** workload runner uses `Stopwatch` — adequate for large-gap confirmation; BDN project covers exact+facet (pass 1), operators-on, WithinBigram A/B, and cold NaturalSort (pass 2 x64). Full CI matrix automation not yet wired.

---

## Environment matrix

| Architecture | Machine | OS / RID | Runtime | Cores | ISA | Notes |
|--------------|---------|----------|---------|------:|-----|-------|
| **x64** | Windows dev (Cray) | win-x64 | .NET 10.0.11 | 12 | AVX2 | Baseline + post-fix |
| **ARM64** | `mac.home` (`ssh macos`) | osx-arm64 | .NET 10.0.10 | 8 | AdvSimd | Post-fix via `git archive` |
| **ARM64** | `homelab` (`ssh homelab`, Ubuntu) | linux-arm64 | .NET 10.0.10 | 4 | AdvSimd | Post-fix via `git archive` |

Common benchmark flags: `--warmup 2 --iterations 5 --seed 1337`. Ingestion: `--ingestion-count 100000 --seed 1337`.

**Remaining gap vs QA full bundle:** automated CI gate script; PGO/tiering flags not recorded.

---

## Baseline architecture (v0.5.5 @ 21dd346)

Facts confirmed in code at baseline:

- **Reads:** lock-free immutable `IndexSnapshot` via `IndexSnapshotProvider`.
- **Writes:** `IndexUpdater` serialises mutations; every publish runs `IndexSnapshotBuilder.Build` on the **full** `_entries` dictionary — no incremental inverted index.
- **Exact single-token (no filter):** posting-span fast path in `SearchEngineSharp.Find`.
- **Exact + facet (baseline):** always `ExecuteQuery` → bitset → `FacetFilterEvaluator.Apply` O(N) ordinal scan.
- **Boolean queries:** `FastBitSet` + `ArrayPool`; AVX2 / AdvSimd / scalar dispatch.
- **Progressive ingestion (baseline):** `Adaptive` policy published every `FixedBatchSize` (2000) on fast scans — ~50 publishes at 100k.

---

## Baseline measurement matrix (v0.5.5)

### Progressive ingestion (100k synthetic paths, fast scan)

Overhead× = total rebuild CPU ÷ one-shot `RebuildFrom` for same 100k set.

| Policy | x64 | macOS ARM64 | Ubuntu ARM64 |
|--------|----:|------------:|-------------:|
| fixed-2k | **25.69×** (50) | **26.21×** | **25.61×** |
| adaptive-k2 | **23.61×** (50) | **25.11×** | **24.64×** |
| debounce-100ms | **1.96×** (2) | **1.71×** | **1.72×** |

Theoretical amplification for 50×2k batch full rebuilds: `2000 × (1+…+50) / 100000 = 25.5×`.

### Query latency — facet scenarios (medium 100k, P50)

| Scenario | x64 P50 | macOS ARM64 P50 | Ubuntu ARM64 P50 |
|----------|--------:|----------------:|-----------------:|
| Exact (text only) | 0.0044 ms | 0.0027 ms | 0.0025 ms |
| Exact + Filter | 0.5313 ms | 0.5333 ms | 0.5348 ms |
| Ratio (Exact+Filter / Exact) | **~121×** | **~197×** | **~214×** |

Exact+Filter P50 was **~0.53 ms on all three hosts** — dominated by full ordinal facet scan.

---

## Assessment summary

| ID | Priority | Evidence | Confidence | Arch | Topic | Status |
|----|----------|----------|------------|------|-------|--------|
| **F-01** | P2 | E2, x64 + ARM64 | High | cross-arch | Progressive ingestion full rebuild amplification | **resolved** (v0.5.6) |
| **F-02** | P2 | E2 + BDN, x64 + ARM64 | High | cross-arch | Facet filter bypasses exact fast path | **resolved** (v0.5.6) |
| **F-03** | P3 | E2 | Medium | cross-arch | Benchmark harness not regression-grade | **substantially remediated** (manual gate; v0.5.6) |
| C-01 | investigation | E0 | — | — | Per-query allocations despite `ArrayPool` | open |
| C-02 | investigation | E0 | — | ARM64 | AdvSimd path benefit unmeasured | open |
| C-03 | investigation | **E2 x64** (cold NaturalSort); concurrent duplicate build E0 | Medium (x64) | x64 measured | Natural sort cold build on new snapshot | **follow-up** (pass 2) |
| C-04 | investigation | E0 | — | — | Regex cache global lock | open |
| C-05 | investigation | E0 | — | — | Peak memory during snapshot build | open |

---

## Findings

### F-01: Progressive ingestion ~25× rebuild amplification

**Baseline status:** confirmed (v0.5.5)  
**Remediation status:** **resolved** (v0.5.6)  
**Architecture/SKU:** x64 and ARM64  
**Evidence grade:** E2  
**Confidence:** high

**Co (baseline):** Each progressive publish calls `IndexSnapshotBuilder.Build` on the entire `_entries` set. With `FixedBatchSize = 2000` and 100k documents, ~50 publishes process ~2.55M document-equivalents instead of 100k (~25× rebuild CPU vs one-shot).

**Jak (implemented):** **Growth-aware publishing** — `GrowthAwareBatchCap` raises batch cap to `max(FixedBatchSize, indexedCount)` after each publish; `MaxStaleness` unchanged as hard freshness cap.

**Resolution evidence:**

- Code: [`IngestPublishOptions.cs`](../../src/SearchEngine.Sharp/Ingestion/IngestPublishOptions.cs), [`ProgressiveIndexIngestion.cs`](../../src/SearchEngine.Sharp/Ingestion/ProgressiveIndexIngestion.cs).
- Measured: **~2.2×** overhead×, **7 publishes**, staleness **≤ 204 ms** — table in [Post-fix verification](#post-fix-verification--before--after).
- Gate G-01: **pass** (< 5× target exceeded with margin).

Set `GrowthAwareBatchCap = false` or use benchmark label `adaptive-fixed-2k` to reproduce pre-fix behaviour (~25×).

---

### F-02: Facet filter bypasses exact-query fast path

**Baseline status:** confirmed (v0.5.5)  
**Remediation status:** **resolved** (v0.5.6)  
**Architecture/SKU:** x64 + ARM64  
**Evidence grade:** E2 + BDN  
**Confidence:** high

**Co (baseline):** `Find(..., FacetFilter?)` never used the single-word exact posting fast path. Every filtered exact query paid full bitset evaluation plus O(N) facet scan (~0.53 ms floor on all hosts).

**Jak (implemented):**

1. Posting-span fast path for single exact term + facet (`TryFindExactWithFacetFastPath`, `TryGetExactFacetCount`).
2. Filter-only returns facet-matching bitset directly (no `RentAllTrueBitSet` + redundant intersect).

**Resolution evidence:**

- Workload P50: **0.5313 ms → 0.0161 ms** (x64); Exact unchanged.
- BDN ratio ExactWithFacetFilter / ExactOnly: **1.39–1.46×** (was effectively ~219× for filter-only path in BDN; exact+facet no longer O(N)).
- Gate G-02: **pass** (P50 < 0.10 ms; Exact regression ≤ 5%).

Filter-only queries remain O(N) by design — documented in [`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md).

---

### F-03: Benchmark harness not yet a regression gate

**Baseline status:** confirmed (v0.5.5)  
**Remediation status:** **substantially remediated** (v0.5.6) — manual gate available; full automated QA bundle not yet complete  
**Evidence grade:** E2  
**Confidence:** medium

**Co (baseline):** Custom `Stopwatch` runner detected large regressions but lacked BDN-grade statistics, environment fingerprint, and raw artifact retention.

**Jak (implemented):** Separate `SearchEngine.Sharp.MicroBenchmarks` BDN project; CSV artifacts per platform under [`artifacts/`](artifacts/README.md); `InProcessNoEmitToolchain` for sibling-repo builds (`32f8b7a`); root `.gitignore` exception so audit CSVs are committed.

**Remaining vs original F-03 acceptance criteria:**

- CI automation for x64 + ARM64 matrix on each release — not wired.
- PGO / tiering flags — not recorded.
- `EnvironmentFingerprint` — runtime, RID, arch, core count, ISA, git SHA; **not** CPU SKU or SDK version.
- Workload runner (`Stopwatch`) retained for large-gap scenarios; BDN covers tight gates on exact+facet microbenchmark only.

---

## Investigation candidates (not findings)

Per [`QA.md`](../../../../audits/dotnet-performance/QA.md): *No severity-bearing performance finding from E0 static evidence.*

| Signal | Location | Why E0 |
|--------|----------|--------|
| **C-01** Per-query GC (`QueryContext`, lists) | [`QueryContext.cs`](../../src/SearchEngine.Sharp/Pooling/QueryContext.cs) | Pool covers `ulong[]` only; BDN now covers exact+facet alloc (~6 KB) but not general query paths |
| **C-02** ARM64 SIMD popcount path | [`FastBitSet.cs`](../../src/SearchEngine.Sharp/Index/FastBitSet.cs) | No ARM64 vs scalar benchmark |
| **C-03** Natural sort cold build on new snapshot | [`IndexSnapshot.cs`](../../src/SearchEngine.Sharp/Snapshots/IndexSnapshot.cs) `GetSortedPermutation` | **E2 x64** (pass 2 BDN, [`pass2-x64-win/`](artifacts/pass2-x64-win/README.md)): cold **23.4 ms**, warm **77 μs** @ 100k; progressive 7× cold **48.4 ms**. Concurrent duplicate cold build on parallel first callers still **E0**. |
| **C-04** Regex LRU global lock | [`RegexPatternCache.cs`](../../src/SearchEngine.Sharp/Query/RegexPatternCache.cs) | Needs concurrent benchmark |
| **C-05** Peak build memory | `IndexSnapshotBuilder` | No RSS/gcdump at 100k/1M |

---

## Regression gates

| Gate | Metric | Threshold | Baseline | Post-fix (v0.5.6) |
|------|--------|-----------|----------|-------------------|
| G-01 ingestion | Rebuild amplification @ 100k | < 10× (target < 5×) | ~25× | **~2.2×** ✓ |
| G-01 ingestion | WorstCaseStaleness | ≤ 1 s | ✓ | ✓ |
| G-02 exact+facet | P50 latency @ 100k seed 1337 | < 0.10 ms | ~0.53 ms | **~0.016 ms** ✓ |
| G-02 exact+facet | Exact (no filter) regression | ≤ 5% | — | **0%** ✓ |
| G-03 infra | Benchmark artifact | SHA, RID, CPU SKU, SDK, PGO, raw CSV, CI matrix | missing | **partial** — BDN CSV committed; fingerprint partial; manual only ✓ |

---

## Not flagged

Confirmed good decisions (do not refactor blindly):

- Flat-array posting/word layout and immutable snapshot reads.
- Exact posting fast path (extended to exact+facet in v0.5.6).
- Bigram pruning for `Within`.
- `ArrayPool` for large bitsets; manual intrinsics with scalar fallback.
- Full rebuild correctness model for `IndexUpdater` — F-01 fix adjusts **publish frequency**, not rebuild architecture.

---

## Routed to sibling audits

| Signal | Owner package |
|--------|---------------|
| Class-level branching / evaluator structure | `object-design` (if refactoring query API) |
| DI / composition | `dependency-injection` (not performance-critical here) |

---

## Verification vs original review

| Original claim | Baseline verification | Post-fix |
|----------------|----------------------|----------|
| F-01 ~25× on x64 | **Confirmed** (25.7×); ARM64 25–26× | **~2.2×**, 7 publishes |
| F-02 ~115× Exact+Filter | **Confirmed** (~121× x64); ~0.53 ms scan floor | **~3.7×** workload; BDN **1.4×** |
| Adaptive does not fix amplification | **Confirmed** at baseline | **Fixed** via `GrowthAwareBatchCap` |
| C-01…C-05 at baseline | **Confirmed** E0 | C-03 cold NaturalSort now **E2 x64** (pass 2); others unchanged |
| No general intrinsics refactor | **Confirmed** | unchanged |

---

## Knowledge and evidence gaps (remaining)

- No automated CI gate script for BDN matrix on x64 + ARM64 (F-03 follow-up).
- `EnvironmentFingerprint` missing CPU SKU and SDK version; PGO/tiering flags not recorded.
- No `dotnet-trace` / disassembly compare for `FastBitSet` ARM64 vs scalar (C-02).
- No peak RSS benchmark for index build at 1M documents (C-05).
- ARM64 pass 2 BDN (operators-on, NaturalSort cold) not yet run.
- Concurrent duplicate NaturalSort build under parallel first callers (C-03 sub-item) still unmeasured.

---

## References (compendium — do not copy content)

- [`dotnet-performance/compendium/references/evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md)
- [`dotnet-performance/compendium/references/severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md)
- [`dotnet-performance/compendium/references/performance-guardrails.md`](../../../../audits/dotnet-performance/compendium/references/performance-guardrails.md)
- [`dotnet-performance/QA.md`](../../../../audits/dotnet-performance/QA.md)
