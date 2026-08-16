# Pass 2 benchmark artifacts (macOS ARM64)

Measured **2026-08-16** on `mac.home` (`ssh macos`). Source of truth: `*-report.csv` in this folder.

Harness:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*OperatorsOn*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*WithinBigram*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*NaturalSortBenchmark*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*ProgressiveNaturalSort*'
```

Environment: macOS ARM64 (AdvSimd), 8 cores, .NET 10.0.10 (`environment-fingerprint.json`). File corpus: `FileSearchDataFactory` @ 100k, seed 2026.

## OperatorsOnBenchmark

| Method | Mean | Ratio vs Exact off |
|--------|-----:|-------------------:|
| Exact_OperatorsOff | **2,255 ns** | 1.00 |
| Exact_OperatorsOn | **2,158 ns** | **0.96** |

Find Exact operators-on/off: no material regression on ARM64.

## WithinBigramBenchmark

Query `tion`, file corpus @ 100k.

| Method | Mean | Ratio |
|--------|-----:|------:|
| First bigram (legacy) | **26.82 μs** | 1.00 |
| Rarest bigram (experimental) | **26.67 μs** | **0.99** |

Rarest-bigram rejected (no win on ARM64 either).

## NaturalSortBenchmark

Within query `report`, `IterationSetup` → fresh snapshot (`InvocationCount=1`).

| Method | Mean |
|--------|-----:|
| Cold — first NaturalSort | **2,956 μs** |
| Warm — cached permutation | **2,928 μs** |
| SnapshotOrder baseline | **45.4 μs** |

**Note:** On this host cold and warm means are similar (~3 ms). Permutation cache benefit is not visible in this BDN row (contrast x64 warm **~77 μs** in [`pass2-x64-win/`](../pass2-x64-win/README.md)). Component benchmark [`pass3-arm64-macos/`](../../pass3-arm64-macos/README.md) still shows large global-permutation cost at K=0 when measured directly.

## ProgressiveNaturalSortBenchmark

Seven growth-aware publishes (2k→100k), cold NaturalSort requery per snapshot.

| Method | Mean |
|--------|-----:|
| 7× cold NaturalSort requery | **6,010 μs** |
| 7× SnapshotOrder requery | **141 μs** |

Ratio NaturalSort / SnapshotOrder: **~43×** (6,010 / 141).

Cross-arch: x64 progressive cold total **~48 ms** vs **~6 ms** here — ARM64 NaturalSort component is much faster on this corpus, but still dominates SnapshotOrder in progressive requery.
