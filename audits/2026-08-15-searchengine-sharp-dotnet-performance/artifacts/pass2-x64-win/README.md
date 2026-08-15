# Pass 2 benchmark artifacts (x64 Windows)

Measured 2026-08-15. Source of truth: the `*-report.csv` files in this folder (BDN `InProcessNoEmitToolchain`, `IterationSetup` for cold NaturalSort benchmarks).

Harness:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*OperatorsOn*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*WithinBigram*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*NaturalSort*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*ProgressiveNatural*'
```

Environment: Windows x64, 12 cores, AVX2, .NET 10.0.11. File corpus: `FileSearchDataFactory` @ 100k, seed 2026.

## OperatorsOnBenchmark (`OperatorsOnBenchmark-report.csv`)

Synthetic facet corpus @ 100k, seed 1337 (same as pass 1 ExactFacet).

| Method | Mean | Ratio vs Exact off |
|--------|-----:|-------------------:|
| Exact_OperatorsOff | **1,387 ns** | 1.00 |
| Exact_OperatorsOn | **1,397 ns** | **1.01** |
| ExactFacet_OperatorsOff | **2,881 ns** | **2.08** |
| ExactFacet_OperatorsOn | **2,891 ns** | **2.08** |
| CountExact_OperatorsOff | **23.7 ns** | 0.02 |
| CountExact_OperatorsOn | **34.5 ns** | 0.02 (**1.46×** vs Count off) |
| CountExactFacet_OperatorsOff | **1,374 ns** | 0.99 |
| CountExactFacet_OperatorsOn | **1,314 ns** | 0.95 |

Find Exact operators-on/off: no material regression. CountExact operators-on adds modest allocation overhead vs off.

## WithinBigramBenchmark (`WithinBigramBenchmark-report.csv`)

Query `tion`, file corpus @ 100k. Production uses legacy first-bigram path; rarest is benchmark-only (`WithinBigramQueryMatcher`).

| Method | Mean | Ratio |
|--------|-----:|------:|
| First bigram (legacy direct path) | **7.40 μs** | 1.00 |
| Rarest bigram (experimental) | **7.93 μs** | **1.07** |

Rarest-bigram rejected for production.

## NaturalSortBenchmark (`NaturalSortBenchmark-report.csv`)

File corpus @ 100k, Within query `report`, `IterationSetup` → fresh snapshot each invocation (`InvocationCount=1`).

| Method | Mean | Ratio vs cold |
|--------|-----:|--------------:|
| Cold — first NaturalSort on fresh snapshot | **23,426 μs** | 1.00 |
| Warm — cached permutation | **77.3 μs** | **0.003** (~303× faster) |
| SnapshotOrder baseline | **48.3 μs** | — |

## ProgressiveNaturalSortBenchmark (`ProgressiveNaturalSortBenchmark-report.csv`)

Seven growth-aware publish sizes (2k→100k), cold NaturalSort requery per fresh snapshot, `IterationSetup` each invocation.

| Method | Mean |
|--------|-----:|
| 7× cold NaturalSort requery | **48,389 μs** |
| 7× SnapshotOrder requery | **134.5 μs** |

Ratio NaturalSort / SnapshotOrder: **~360×** (48,389 / 134.5).

Progressive file-search UI with `NaturalSortAscending` on every publish: expect **~48 ms** cold-sort work per full 100k scan (7 publishes), vs **~135 μs** if SnapshotOrder were used instead.

## ARM64

Pass 2 CSVs above are **x64 only**. ARM64 pass 2 not yet measured.
