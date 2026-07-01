# Progressive ingestion policy report

Measured on the development machine (X64, 12 logical cores, .NET 10) using
`SyntheticPathFeed` tokenized paths (~6–7 tokens per file).

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
| `FixedBatchSize` | 2,000 |
| `MinInterval` | 100 ms |
| `AdaptiveMultiplier` (k) | 2.0 |
| `MaxStaleness` | 1 s |
| `MinTimerPublishBatch` | 200 |

### Publish triggers

1. **Batch cap** — publish when the buffer reaches 2,000 entries (primary path during fast scans).
2. **Staleness cap** — publish when the oldest buffered entry has waited ≥ `MaxStaleness` (guarantees UI freshness during slow I/O).
3. **Adaptive pacing** — after the interval `max(MinInterval, k × lastRebuildDuration)` elapses, publish if the buffer has at least `MinTimerPublishBatch` entries (prevents rebuild thrash while keeping progressive updates).

## Results — 100,000 paths (fast scan, no artificial I/O delay)

| Policy | Scan wall (ms) | Rebuild CPU (ms) | Publishes | Worst staleness (ms) | Overhead×* |
|---|---:|---:|---:|---:|---:|
| per-entry | *(skipped at 100k)* | — | — | — | — |
| fixed-2k | 4,185 | 4,138 | 50 | 927 | 24.8 |
| debounce-100ms | 463 | 329 | 2 | 140 | 1.8 |
| **adaptive-k2** | **4,547** | **4,517** | **50** | **1,001** | **25.0** |

\*Overhead× = total rebuild CPU ÷ one-shot `RebuildFrom` for the same 100k set (~167 ms).

**Demo (100k, adaptive):** 50 progressive publishes, worst staleness **848 ms**, rebuild CPU **4,337 ms**.

## Baseline — 2,000 paths (per-entry feasible)

| Policy | Scan wall (ms) | Rebuild CPU (ms) | Publishes | Worst staleness (ms) | Overhead× |
|---|---:|---:|---:|---:|---:|
| **per-entry** | 2,448 | 2,426 | 2,000 | 2,432 | **1,313** |
| fixed-2k | 5 | 2 | 1 | 3 | 1.4 |
| debounce-100ms | 6 | 3 | 1 | 3 | 1.9 |
| adaptive-k2 | 11 | 2 | 1 | 9 | 1.0 |

Per-entry at 2k already spends **~2.4 s** in rebuilds for a scan that completes in milliseconds. Extrapolated to 100k without batching: **hours** of rebuild CPU (O(N²) total work).

## Slow scan note (1 ms simulated I/O per entry)

With `TimeDebounce` and a trickle feed, timer-only publishing emits **many small batches** (one per `MinInterval`), recreating quadratic rebuild cost. That is why pure debounce was rejected as the default.

`Adaptive` adds `MinTimerPublishBatch` and `MaxStaleness` so slow scans batch meaningfully while keeping staleness bounded. A full 100k × 1 ms benchmark run is dominated by the artificial 100 s sleep; wall time ≈ scan time + rebuild CPU (same order as fast scan rebuild totals).

## Rejected alternatives

| Policy | Reason |
|---|---|
| **Per-entry** | Correct but unusable at scale: O(N²) rebuild work (1,313× overhead at 2k; projected hours at 100k). |
| **Fixed batch only** | Excellent throughput and 50 progressive updates at 100k, but no staleness cap during very slow scans (last partial batch could wait indefinitely). |
| **Pure time debounce** | On fast scans: only **2** publishes for 100k (poor progressive UX). On slow scans without batch guards: micro-publishes every 100 ms → rebuild thrash. |

## Architecture summary

```
scanner (IAsyncEnumerable)
    → bounded Channel (backpressure when full)
    → publisher (batch + policy)
    → IIndexUpdater.AddOrUpdateEntries (single rebuild per publish)
    → IndexSnapshotProvider.Publish

queries (ISearchEngine) ── lock-free read of current snapshot
```

- **Final flush** always runs on successful scan completion.
- **Cancellation** optionally flushes the buffer (`FlushOnCancellation`, default `true`).
- **Scan errors** complete the channel with the exception after the publisher drains.

## Recommendation for UI integration

```csharp
var ingestion = new ProgressiveIndexIngestion(updater);
var result = await ingestion.IngestAsync(
    ScanFilesAsync(root, ct),
    new IngestPublishOptions(), // adaptive defaults
    onPublished: _ => dispatcher.Invoke(RefreshResults));
```

Debounce user keystrokes separately (150–300 ms); ingestion debouncing is independent and controls index freshness.
