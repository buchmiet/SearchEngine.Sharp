# Progressive ingestion policy report

Measured with `SyntheticPathFeed` tokenized paths (~6–7 tokens per file), seed **1337**, 100k entries, fast scan (no artificial I/O delay).

Harness:

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000 --seed 1337
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 2000 --seed 1337
```

Demo:

```bash
dotnet run -c Release --project demos/ProgressiveIngestion.Demo -- --count 100000 --scan-delay-ms 0
```

## Chosen policy: **Adaptive** (default in `IngestPublishOptions`)

| Parameter | Value |
|---|---|
| `Policy` | `Adaptive` |
| `FixedBatchSize` | 2,000 (initial batch cap) |
| `GrowthAwareBatchCap` | **true** (default since 0.5.6) |
| `MinInterval` | 100 ms |
| `AdaptiveMultiplier` (k) | 2.0 |
| `MaxStaleness` | 1 s |
| `MinTimerPublishBatch` | 200 |

### Publish triggers

1. **Growth-aware batch cap** — after each publish, cap becomes `max(FixedBatchSize, indexedDocumentCount)`. On a fast 100k feed this yields snapshot sizes roughly **2k → 4k → 8k → 16k → 32k → 64k → 100k** (~7 publishes instead of 50).
2. **Staleness cap** — publish when the oldest buffered entry has waited ≥ `MaxStaleness`.
3. **Adaptive pacing** — after `max(MinInterval, k × lastRebuildDuration)`, publish if the buffer has at least `MinTimerPublishBatch` entries.

Set `GrowthAwareBatchCap = false` to restore fixed 2k batch behaviour (`adaptive-fixed-2k` in the benchmark harness).

## Results — 100,000 paths (post-fix, v0.5.6+)

Measured 2026-08-15 on Windows x64 (.NET 10.0.11). ARM64 runs gave the same publish count (7) and similar overhead×.

| Policy | Rebuild CPU (ms) | Publishes | Worst staleness (ms) | Overhead×* |
|---|---:|---:|---:|---:|
| fixed-2k | 4,486 | 50 | 886 | 28.6 |
| debounce-100ms | 296 | 2 | 146 | 1.7 |
| **adaptive** | **349** | **7** | **158** | **2.2** |
| adaptive-fixed-2k | 4,005 | 50 | 860 | 25.6 |

\*Overhead× = total rebuild CPU ÷ one-shot `RebuildFrom` for the same 100k set (~157 ms this run).

Theoretical amplification for growth-aware series: `(2+4+8+16+32+64+100)/100 = **2.26×**` — matches measured **~2.2×**.

### Cross-platform check (adaptive, 100k)

| Platform | Overhead× | Publishes | Worst staleness (ms) |
|----------|----------:|----------:|---------------------:|
| Windows x64 | 2.22 | 7 | 158 |
| macOS ARM64 | 2.21 | 7 | 179 |
| Ubuntu ARM64 | 2.13 | 7 | 204 |

All gates from the performance audit met: amplification **< 10×** (target **< 5×** exceeded), staleness **≤ 1 s**.

## Historical baseline — pre-0.5.6 (fixed 2k adaptive)

Before `GrowthAwareBatchCap`, adaptive behaved like fixed-2k on fast scans:

| Policy | Rebuild CPU (ms) | Publishes | Overhead× |
|---|---:|---:|---:|
| adaptive (old) | ~4,500 | 50 | **~25×** |

Per-entry at 2k paths still shows **~1,313×** overhead — per-entry remains a diagnostic baseline only.

## Slow scan note (1 ms simulated I/O per entry)

Pure time debounce without batch guards recreates quadratic rebuild cost on slow scans. `Adaptive` keeps `MinTimerPublishBatch` and `MaxStaleness` so slow scans batch meaningfully while keeping staleness bounded.

## Rejected alternatives

| Policy | Reason |
|---|---|
| **Per-entry** | Correct but unusable at scale: O(N²) rebuild work. |
| **Fixed batch only** | Good throughput but no staleness cap during very slow scans. |
| **Pure time debounce** | On fast scans: too few publishes; on slow scans without guards: rebuild thrash. |

## Architecture summary

```
scanner (IAsyncEnumerable)
    → bounded Channel (backpressure when full)
    → publisher (growth-aware batch + policy)
    → IIndexUpdater.AddOrUpdateEntries (single rebuild per publish)
    → IndexSnapshotProvider.Publish

queries (ISearchEngine) ── lock-free read of current snapshot
```

## Recommendation for UI integration

```csharp
var ingestion = new ProgressiveIndexIngestion(updater);
var result = await ingestion.IngestAsync(
    ScanFilesAsync(root, ct),
    new IngestPublishOptions(), // adaptive + growth-aware defaults
    onPublished: _ => dispatcher.Invoke(RefreshResults));
```

Debounce user keystrokes separately (150–300 ms); ingestion debouncing is independent and controls index freshness.
