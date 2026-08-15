# .NET Performance Review — SearchEngine.Sharp (ARM64/x64)

**Target:** `SearchEngine.Sharp` @ `21dd3463a0fc8b8298295c160e9af703b56720ac` (v0.5.5, .NET 10)  
**Audit package:** `dotnet-performance` (`buchmiet/audits`, branch `markdown-first-retrieval`)  
**Date:** 2026-08-15  
**Phase 0:** no prior runs in this repo (first committed report)

This report merges the initial external review with an independent verification pass (code trace + benchmark reproduction on x64 and ARM64).

Methodology gates applied per [`evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md) and [`severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md): E0 static signals are investigation candidates only; performance findings require E2+ with architecture-specific baselines.

---

## Scope and limitations

- **In scope:** query hot paths, progressive ingestion rebuild cost, facet filtering, benchmark harness maturity, SIMD dispatch posture.
- **Out of scope (this run):** production telemetry (E4), BDN microbenchmark suite, disassembly review, peak RSS during index build, allocation-rate measurement.
- **Harness limitation:** existing benchmarks use `Stopwatch` + forced `GC.Collect()` — sufficient for ~25× and ~120× gaps ([`F-03`](#f-03-benchmark-harness-not-yet-a-regression-gate)), insufficient for 3–10% gates without BDN + full fingerprint ([`QA.md`](../../../../audits/dotnet-performance/QA.md) acceptance criteria).

---

## Environment matrix

| Architecture | Machine | OS / RID | Runtime | Cores | ISA | Notes |
|--------------|---------|----------|---------|------:|-----|-------|
| **x64** | Windows dev (Cray) | win-x64 | .NET 10.0.11 | 12 | AVX2 | Original report machine class |
| **ARM64** | `mac.home` (`ssh macos`) | osx-arm64 | .NET 10.0.10 | 8 | AdvSimd | Repo copied via `git archive` |
| **ARM64** | `homelab` (`ssh homelab`, Ubuntu) | linux-arm64 | .NET 10.0.10 | 4 | AdvSimd | Repo copied via `git archive` |

Common benchmark flags: `--warmup 2 --iterations 5 --seed 1337`. Ingestion: `--ingestion-count 100000 --seed 1337`.

**Not recorded (gap vs QA minimum bundle):** CPU SKU model string, PGO/tiering flags, power state, raw CSV/JSON artifacts, git artifact hash of built binaries.

---

## As-Is Performance Architecture

Facts confirmed in code and docs:

- **Reads:** lock-free immutable `IndexSnapshot` via `IndexSnapshotProvider`.
- **Writes:** `IndexUpdater` serialises mutations; every publish runs `IndexSnapshotBuilder.Build` on the **full** `_entries` dictionary — no incremental inverted index ([`IndexUpdater.cs`](../../src/SearchEngine.Sharp/IndexUpdater.cs)).
- **Exact single-token (no filter):** posting-span fast path in `SearchEngineSharp.Find` — no bitset ([`SearchEngineSharp.cs`](../../src/SearchEngine.Sharp/SearchEngineSharp.cs)).
- **Exact + facet:** always `ExecuteQuery` → bitset → `FacetFilterEvaluator.Apply` O(N) ordinal scan ([`FacetFilterEvaluator.cs`](../../src/SearchEngine.Sharp/Query/FacetFilterEvaluator.cs)).
- **Boolean queries:** `FastBitSet` + `ArrayPool`; AVX2 / AdvSimd / scalar dispatch ([`FastBitSet.cs`](../../src/SearchEngine.Sharp/Index/FastBitSet.cs)).
- **Progressive ingestion:** `ProgressiveIndexIngestion` batches into `AddOrUpdateEntries`; default `Adaptive` policy still publishes every `FixedBatchSize` (2000) on fast scans ([`ProgressiveIndexIngestion.cs`](../../src/SearchEngine.Sharp/Ingestion/ProgressiveIndexIngestion.cs), [`IngestPublishOptions.cs`](../../src/SearchEngine.Sharp/Ingestion/IngestPublishOptions.cs)).
- **Existing workload reports:** [`docs/ingestion-policy-report.md`](../../docs/ingestion-policy-report.md), [`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md).

Overall posture: mature engine with deliberate optimisations; two architectural boundaries dominate measured cost — not a “rewrite intrinsics everywhere” codebase.

---

## Measurement matrix

### Progressive ingestion (100k synthetic paths, fast scan)

Overhead× = total rebuild CPU ÷ one-shot `RebuildFrom` for same 100k set.

| Policy | x64 (this run) | macOS ARM64 | Ubuntu ARM64 | Original review (x64) |
|--------|---------------:|------------:|-------------:|----------------------:|
| fixed-2k | **25.69×** (50 publishes) | **26.21×** | **25.61×** | 24.8× |
| adaptive-k2 | **23.61×** (50 publishes) | **25.11×** | **24.64×** | 25.0× |
| debounce-100ms | **1.96×** (2 publishes) | **1.71×** | **1.72×** | 1.8× |

Theoretical amplification for 50×2k batch full rebuilds: `2000 × (1+…+50) / 100000 = 25.5×` — matches all platforms.

**Command:**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000 --seed 1337
```

### Query latency — facet scenarios (medium 100k, P50)

| Scenario | x64 P50 | macOS ARM64 P50 | Ubuntu ARM64 P50 | Original review P50 |
|----------|--------:|----------------:|-----------------:|--------------------:|
| Exact (text only) | 0.0044 ms | 0.0027 ms | 0.0025 ms | 0.0044 ms |
| Exact + Filter | 0.5313 ms | 0.5333 ms | 0.5348 ms | 0.5049 ms |
| Ratio (Exact+Filter / Exact) | **~121×** | **~197×** | **~214×** | **~115×** |

Exact+Filter P50 is **~0.53 ms on all three hosts** — dominated by full ordinal facet scan, not exact lookup variance.

**Command:**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet --warmup 2 --iterations 5 --seed 1337
```

### Query throughput — facet (250k, q/s)

| Scenario | x64 | macOS ARM64 | Ubuntu ARM64 | Original review |
|----------|----:|------------:|-------------:|----------------:|
| Exact + Filter | 712 | 737 | 735 | 694 |
| Filter-only | 839 | 505 | 623 | 398 |

Filter-only ranking vs Exact+Filter **varies by platform and harness noise**; the structural defect (O(N) facet scan bypassing exact fast path) is stable. Do not use throughput alone as a gate without P50/P99 from BDN.

---

## Assessment summary

| ID | Priority | Evidence | Confidence | Arch | Topic | Status |
|----|----------|----------|------------|------|-------|--------|
| **F-01** | P2 | E2, x64 + ARM64 | High | cross-arch | Progressive ingestion full rebuild amplification | **confirmed** |
| **F-02** | P2 | E2, x64 + ARM64 | High | cross-arch | Facet filter bypasses exact fast path | **confirmed** |
| **F-03** | P3 | E1/E2 | Medium | cross-arch | Benchmark harness not regression-grade | **confirmed** |
| C-01 | investigation | E0 | — | — | Per-query allocations despite `ArrayPool` | open |
| C-02 | investigation | E0 | — | ARM64 | AdvSimd path benefit unmeasured | open |
| C-03 | investigation | E0 | — | — | Concurrent natural-sort cold start | open |
| C-04 | investigation | E0 | — | — | Regex cache global lock | open |
| C-05 | investigation | E0 | — | — | Peak memory during snapshot build | open |

---

## Findings

### F-01: Progressive ingestion ~25× rebuild amplification

**Status:** confirmed  
**Architecture/SKU:** x64 and ARM64 (Ubuntu + macOS); same mechanism on all measured hosts  
**Claim type:** measured waste on a proven ingestion path  
**Evidence grade:** E2  
**Confidence:** high

**Co:** Each progressive publish calls `IndexSnapshotBuilder.Build` on the entire `_entries` set. With `FixedBatchSize = 2000` and 100k documents, ~50 publishes process ~2.55M document-equivalents instead of 100k (~25× rebuild CPU vs one-shot).

**Evidence:**

- Code: `IndexUpdater.AddOrUpdateEntries` → `RebuildAndPublish` → full build ([`IndexUpdater.cs`](../../src/SearchEngine.Sharp/IndexUpdater.cs) L163–182, L215–218).
- Policy: `Adaptive` uses the same batch cap as `FixedBatch` for fast scans ([`ProgressiveIndexIngestion.cs`](../../src/SearchEngine.Sharp/Ingestion/ProgressiveIndexIngestion.cs) L335–344).
- Benchmarks: table above; aligns with [`docs/ingestion-policy-report.md`](../../docs/ingestion-policy-report.md).

**Mechanism:** O(sum of index sizes) across publishes ≈ O(N²/batch) for fixed batch size N/batch times. Adaptive timer pacing does not increase batch size — it only spaces timer-triggered publishes.

**Jak:** First experiment: **growth-aware / geometric publishing** — next publish after ~proportional index growth (e.g. 2k → 4k → 8k → …), keeping `MaxStaleness` as hard freshness cap. Avoid incremental mutable inverted index as first step.

**Dlaczego:** Ingestion is explicitly full-rebuild by design comment; progressive UX requires batching but current fixed batch cap recreates quadratic work ([`IndexUpdater.cs`](../../src/SearchEngine.Sharp/IndexUpdater.cs) L9–15). Measured waste gate per [`severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md) P2.

**Acceptance criteria (proposed gate):**

- 100k synthetic paths, each architecture separately.
- `WorstCaseStaleness ≤ 1 s` (unchanged product constraint).
- Rebuild amplification **< 10×** initially, target **< 5×**.
- No regression in one-shot `RebuildFrom` time > 5%.

---

### F-02: Facet filter bypasses exact-query fast path

**Status:** confirmed  
**Architecture/SKU:** x64 + ARM64; Exact+Filter P50 ~0.53 ms stable across hosts  
**Claim type:** measured waste on selective query path  
**Evidence grade:** E2  
**Confidence:** high

**Co:** `Find(..., FacetFilter?)` never uses the single-word exact posting fast path. Every filtered exact query pays full bitset evaluation plus O(N) facet scan.

**Evidence:**

- Fast path exists only on overload **without** filter ([`SearchEngineSharp.cs`](../../src/SearchEngine.Sharp/SearchEngineSharp.cs) L69–88 vs L99–117).
- `FacetFilterEvaluator.Apply` iterates `ordinal = 0 .. DocumentCount-1` ([`FacetFilterEvaluator.cs`](../../src/SearchEngine.Sharp/Query/FacetFilterEvaluator.cs) L26–32).
- P50 gap ~115–214× (Exact vs Exact+Filter) — table above; original [`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md).

**Mechanism:** Selective exact query with few posting hits still scans all documents for facet predicates, then intersects bitsets.

**Jak:**

1. **Primary:** specialised fast path — single exact term + facet + snapshot order: after resolving posting span, evaluate facet predicates **only for ordinals in the posting list**; same for `CountMatches`.
2. **Secondary:** filter-only — return facet-matching bitset directly instead of `RentAllTrueBitSet` + second bitset + intersect ([`SearchEngineSharp.cs`](../../src/SearchEngine.Sharp/SearchEngineSharp.cs) L159–175, follow-up noted in [`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md)).

**Dlaczego:** Posting fast path is the engine’s main selective-query optimisation; facet post-processing negates it ([`evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md) — isolate mechanism after profile proves relevance; here E2 shows ~0.53 ms floor = scan cost).

**Acceptance criteria (proposed experimental gate):**

- 100k / seed 1337, per architecture.
- Exact+Filter P50 **< 0.10 ms** (experimental threshold vs current ~0.53 ms).
- Exact (no filter) regression **≤ 5%**.

---

### F-03: Benchmark harness not yet a regression gate

**Status:** confirmed  
**Architecture/SKU:** cross-architecture  
**Claim type:** infrastructure / measurement maturity  
**Evidence grade:** E1 (harness design) + E2 (large-gap detection only)  
**Confidence:** medium

**Co:** Custom `Stopwatch` runner detects large regressions but lacks BDN-grade statistics, environment fingerprint, and raw artifact retention required for tight gates.

**Evidence:** [`benchmarks/SearchEngine.Sharp.Benchmarks/Program.cs`](../../benchmarks/SearchEngine.Sharp.Benchmarks/Program.cs) — per-query `Stopwatch`, `GC.Collect()` before measure, no CPU SKU / PGO / CSV export. Docs report arch/cores/SIMD only ([`docs/glob-and-facets-report.md`](../../docs/glob-and-facets-report.md)).

**Jak:** Keep existing runner as **workload benchmark**; add separate BenchmarkDotNet project for: `FastBitSet`, exact+facet fast path, facet evaluator, query allocations, SIMD paths, natural sort. Store raw results per [`QA.md`](../../../../audits/dotnet-performance/QA.md) acceptance / [`evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md) minimum bundle.

**Dlaczego:** [`severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md) — single undisclosed microbenchmark caps at P3; ARM64 and x64 require separate baselines (rule 4).

**Acceptance criteria:** CI or manual gate script records SHA, RID, CPU, SDK, raw BDN output for x64 and ARM64 matrix before enforcing numeric thresholds on F-01/F-02 fixes.

---

## Investigation candidates (not findings)

Per [`QA.md`](../../../../audits/dotnet-performance/QA.md): *No severity-bearing performance finding from E0 static evidence.*

| Signal | Location | Why E0 |
|--------|----------|--------|
| **C-01** Per-query GC (`QueryContext`, lists) | [`QueryContext.cs`](../../src/SearchEngine.Sharp/Pooling/QueryContext.cs) | Pool covers `ulong[]` only; no B/op measurement |
| **C-02** ARM64 SIMD popcount path | [`FastBitSet.cs`](../../src/SearchEngine.Sharp/Index/FastBitSet.cs) `GetTrueCountAdvSimd` | No ARM64 vs scalar benchmark |
| **C-03** Natural sort cold concurrent build | [`IndexSnapshot.cs`](../../src/SearchEngine.Sharp/Snapshots/IndexSnapshot.cs) `GetSortedPermutation` | Correct but may duplicate work; unmeasured |
| **C-04** Regex LRU global lock | [`RegexPatternCache.cs`](../../src/SearchEngine.Sharp/Query/RegexPatternCache.cs) | O(vocabulary) scan may dominate; needs concurrent benchmark |
| **C-05** Peak build memory | `IndexSnapshotBuilder` (dict + flat arrays + facet columns) | No RSS/gcdump at 100k/1M |

---

## To-Be Performance Architecture

Recommended work order (risk / reward):

1. **F-02 fast path** — local change, ~120× measured gap, low regression risk to unfiltered exact path.
2. **F-01 growth-aware publishing** — policy change in `ProgressiveIndexIngestion` / options; largest scaling win for directory scans.
3. **F-03 BDN + fingerprint matrix** — enable safe gates for steps 1–2 and future SIMD/allocation work.
4. Measure C-01…C-05 only after gates exist; do not rewrite intrinsics without ARM64+x64 disassembly/benchmark evidence ([`simd-api-selection.md`](../../../../audits/dotnet-performance/compendium/references/simd-api-selection.md)).

---

## Regression gates (proposed)

| Gate | Metric | Threshold | Arch |
|------|--------|-----------|------|
| G-01 ingestion | Rebuild amplification @ 100k | < 10× (target < 5×) | x64 + ARM64 separately |
| G-01 ingestion | WorstCaseStaleness | ≤ 1 s | both |
| G-02 exact+facet | P50 latency @ 100k seed 1337 | < 0.10 ms | both |
| G-02 exact+facet | Exact (no filter) regression | ≤ 5% | both |
| G-03 infra | Benchmark artifact | SHA, RID, CPU, SDK, raw JSON/CSV | both |

---

## Not flagged

Confirmed good decisions (do not refactor blindly):

- Flat-array posting/word layout and immutable snapshot reads.
- Exact posting fast path (when no facet filter).
- Bigram pruning for `Within`.
- `ArrayPool` for large bitsets; manual intrinsics with scalar fallback.
- Full rebuild correctness model for `IndexUpdater` — problem is **publish frequency**, not rebuild itself.

---

## Routed to sibling audits

| Signal | Owner package |
|--------|---------------|
| Class-level branching / evaluator structure | `object-design` (if refactoring query API) |
| DI / composition | `dependency-injection` (not performance-critical here) |

---

## Verification vs original review

| Original claim | Verification |
|----------------|--------------|
| F-01 ~25× on x64 | **Confirmed** (25.7× this run); **also on ARM64** (25–26×) |
| F-02 ~115× Exact+Filter | **Confirmed** on x64 (~121×); ARM64 ratio higher because Exact is faster, facet scan unchanged (~0.53 ms) |
| Adaptive does not fix amplification on fast scan | **Confirmed** — same 50 publishes as fixed-2k |
| Filter-only slowest at 250k | **Partially confirmed** — true on ARM64; inverted on x64 Windows this run (harness variance) |
| C-01…C-05 remain E0 | **Confirmed** per [`severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md) |
| No general intrinsics refactor | **Confirmed** — no E2 evidence for SIMD change |

---

## Knowledge and evidence gaps

Append to package GAPS on next audit iteration:

- No BDN project or allocation diagnoser run for `QueryContext`.
- No `dotnet-trace` / disassembly compare for `FastBitSet` ARM64 vs scalar.
- No peak RSS benchmark for index build at 1M documents.
- Benchmark raw artifacts not yet stored under `audits/.../artifacts/` (recommended follow-up).

---

## References (compendium — do not copy content)

- [`dotnet-performance/compendium/references/evidence-and-benchmarking.md`](../../../../audits/dotnet-performance/compendium/references/evidence-and-benchmarking.md)
- [`dotnet-performance/compendium/references/severity-model.md`](../../../../audits/dotnet-performance/compendium/references/severity-model.md)
- [`dotnet-performance/compendium/references/performance-guardrails.md`](../../../../audits/dotnet-performance/compendium/references/performance-guardrails.md)
- [`dotnet-performance/QA.md`](../../../../audits/dotnet-performance/QA.md)
