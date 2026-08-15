# Pass 2 benchmark artifacts (x64 Windows)

Measured 2026-08-15 after review fixes (cold NaturalSort via `IterationSetup`, legacy first-bigram A/B in benchmark assembly).

| CSV | Benchmark |
|-----|-----------|
| `OperatorsOnBenchmark-report.csv` | Exact/Exact+Facet/Count operators on vs off @ 100k synthetic |
| `WithinBigramBenchmark-report.csv` | Legacy first-bigram vs experimental rarest-bigram (`tion`, file corpus) |
| `NaturalSortBenchmark-report.csv` | Cold vs warm NaturalSort on fresh snapshot (`IterationSetup`) |
| `ProgressiveNaturalSortBenchmark-report.csv` | 7 progressive snapshots, cold NaturalSort requery per publish |

## Key results (x64, .NET 10.0.11)

**OperatorsOn** — single-token exact fast path with `enableOperators:true`:
- Exact off/on: **1.02×** (~1.27 μs / ~1.29 μs)
- Exact+Facet off/on: **~2.47×** vs Exact off (posting fast path; not O(N) facet scan)

**WithinBigram** — rarest-bigram rejected for production:
- Legacy first-bigram: **7.41 μs**
- Experimental rarest: **7.96 μs** (**1.07×** slower)

**NaturalSort** (100k file corpus, `report` Within query):
- **Cold** (fresh snapshot): **~22 ms**
- **Warm** (cached permutation): **~68 μs** (~**320×** faster)
- SnapshotOrder baseline: **~48 μs**

**ProgressiveNaturalSort** (7 growth-aware publishes, cold requery each):
- 7× NaturalSort: **~47.5 ms** total (~**6.8 ms/publish**)
- 7× SnapshotOrder: **~125 μs** total
- NaturalSort overhead vs SnapshotOrder in this scenario: **~380×**

Conclusion: cold NaturalSort build dominates progressive file-search requery; not a release blocker for operators-on / bigram dedupe, but documents follow-up candidate for 0.5.7+.
