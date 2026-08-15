# Post-fix benchmark artifacts

Measured after performance remediation commits (`0d0efb3` F-01/F-02, `32f8b7a` BDN harness).

**Harness (ingestion / workload):**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000 --seed 1337
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet --warmup 2 --iterations 5 --seed 1337
```

**Harness (BDN exact+facet microbenchmark):**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*ExactFacet*'
```

**Environment fingerprint (optional, per platform):**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --fingerprint audits/2026-08-15-searchengine-sharp-dotnet-performance/artifacts/x64-win/environment-fingerprint.json
```

Pass a **directory** or a **`.json` file path**. Default (no argument) writes to `benchmarks/SearchEngine.Sharp.MicroBenchmarks/BenchmarkDotNet.Artifacts/environment-fingerprint.json`.

| Folder | Platform | RID | .NET |
|--------|----------|-----|------|
| `x64-win/` | Windows x64, 12 cores, AVX2 | win-x64 | 10.0.11 |
| `arm64-macos/` | mac.home, 8 cores, AdvSimd | osx-arm64 | 10.0.10 |
| `arm64-linux/` | homelab Ubuntu, 4 cores, AdvSimd | linux-arm64 | 10.0.10 |
| `competitor-benchmarks/` | SearchEngine.Sharp version comparison (x64 + ARM64); external libs planned | mixed | see README |

## Contents

| File | Description |
|------|-------------|
| `*/ExactFacetBenchmark-report.csv` | BDN CSV export (`ExactOnly`, `ExactWithFacetFilter`, `FilterOnly`) |
| `x64-win/*-report-github.md` | BDN Markdown summary (x64 only) |
| `x64-win/environment-fingerprint.json` | Runtime/RID/arch/core count/ISA/git SHA (partial — no CPU SKU or SDK version) |
| `pass2-x64-win/*.csv` | Pass 2 BDN: OperatorsOn, WithinBigram A/B, cold NaturalSort, progressive requery |
| `pass3-x64-win/*.csv` | Pass 3 BDN: selectivity sweep (bitset materialization, NaturalSort vs K, facet vs K, E2E pipeline, FileMask glob, build allocation) |

Pass 2 summary: [`pass2-x64-win/README.md`](pass2-x64-win/README.md).  
Pass 3 summary: [`pass3-x64-win/README.md`](pass3-x64-win/README.md).

## F-03 evidence status

These CSVs satisfy the **manual** post-fix evidence gate for exact+facet microbenchmarks. The full QA minimum bundle (CPU SKU, SDK version, PGO/tiering flags, automated CI matrix) is **not** complete — see audit report F-03 status: *substantially remediated*.

**Note:** Root `.gitignore` previously ignored all `artifacts/` paths; audit evidence is explicitly un-ignored via `!audits/**/artifacts/**`.
