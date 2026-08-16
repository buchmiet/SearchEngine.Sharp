# Pass 2 benchmark artifacts (Ubuntu ARM64 / homelab)

Measured **2026-08-16** on homelab (`ssh homelab`). Source of truth: `*-report.csv` in this folder.

Harness: same as [`pass2-arm64-macos/README.md`](../pass2-arm64-macos/README.md).

Environment: Ubuntu 26.04 ARM64 (AdvSimd), 4 cores, .NET 10.0.10 (`environment-fingerprint.json`). File corpus: `FileSearchDataFactory` @ 100k, seed 2026.

## OperatorsOnBenchmark

| Method | Mean | Ratio vs Exact off |
|--------|-----:|-------------------:|
| Exact_OperatorsOff | **2,272 ns** | 1.00 |
| Exact_OperatorsOn | **2,347 ns** | **1.03** |

Find Exact operators-on/off: no material regression on ARM64.

## WithinBigramBenchmark

Query `tion`, file corpus @ 100k.

| Method | Mean | Ratio |
|--------|-----:|------:|
| First bigram (legacy) | **26.69 μs** | 1.00 |
| Rarest bigram (experimental) | **26.68 μs** | **1.00** |

Rarest-bigram rejected.

## NaturalSortBenchmark

Within query `report`, `IterationSetup` → fresh snapshot (`InvocationCount=1`).

| Method | Mean |
|--------|-----:|
| Cold — first NaturalSort | **3,267 μs** |
| Warm — cached permutation | **3,269 μs** |
| SnapshotOrder baseline | **45.6 μs** |

Warm ≈ cold on this host (see macOS README note). Compare x64 [`pass2-x64-win/`](../pass2-x64-win/README.md).

## ProgressiveNaturalSortBenchmark

| Method | Mean |
|--------|-----:|
| 7× cold NaturalSort requery | **6,658 μs** |
| 7× SnapshotOrder requery | **150 μs** |

Ratio NaturalSort / SnapshotOrder: **~45×** (6,658 / 150).
