using System.Diagnostics;
using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Benchmarks;

/// <summary>
/// Compares ingestion publish policies at large scale.
/// </summary>
internal static class IngestionPolicyBenchmark
{
    internal static void Run(int documentCount, int seed, int scanDelayMs)
    {
        Console.WriteLine($"=== ingestion policy comparison — {documentCount:N0} tokenized paths ===");
        if (scanDelayMs > 0)
            Console.WriteLine($"(simulated scan delay: {scanDelayMs} ms/entry)");
        Console.WriteLine($"{"Policy",-18} {"Scan ms",10} {"Rebuild ms",11} {"Publishes",10} {"Staleness ms",13} {"Overhead×",10}");
        Console.WriteLine(new string('-', 78));

        RunScenario("per-entry", documentCount, seed, scanDelayMs, new IngestPublishOptions
        {
            Policy = IngestPublishPolicy.PerEntry,
        }, runOnlyIfSmall: true);

        RunScenario("fixed-2k", documentCount, seed, scanDelayMs, new IngestPublishOptions
        {
            Policy = IngestPublishPolicy.FixedBatch,
            FixedBatchSize = 2_000,
        });

        RunScenario("debounce-100ms", documentCount, seed, scanDelayMs, new IngestPublishOptions
        {
            Policy = IngestPublishPolicy.TimeDebounce,
            MinInterval = TimeSpan.FromMilliseconds(100),
            FixedBatchSize = 50_000,
        });

        RunScenario("adaptive-k2", documentCount, seed, scanDelayMs, new IngestPublishOptions
        {
            Policy = IngestPublishPolicy.Adaptive,
            FixedBatchSize = 2_000,
            MinInterval = TimeSpan.FromMilliseconds(100),
            AdaptiveMultiplier = 2.0,
        });

        Console.WriteLine();
    }

    private static void RunScenario(
        string label,
        int documentCount,
        int seed,
        int scanDelayMs,
        IngestPublishOptions options,
        bool runOnlyIfSmall = false)
    {
        if (runOnlyIfSmall && documentCount > 2_000)
        {
            Console.WriteLine($"{label,-18} {"skipped (>2k)",10}");
            return;
        }

        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var ingestion = new ProgressiveIndexIngestion(updater);

        var scanStarted = Stopwatch.GetTimestamp();
        var result = ingestion.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(
                documentCount,
                seed,
                delayPerEntry: TimeSpan.FromMilliseconds(scanDelayMs)),
            options).GetAwaiter().GetResult();
        double scanMs = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;

        double oneShotMs = MeasureOneShotRebuildMs(documentCount, seed);
        double overhead = oneShotMs > 0 ? result.TotalRebuildCpu.TotalMilliseconds / oneShotMs : 0;

        Console.WriteLine(
            $"{label,-18} {scanMs,10:F0} {result.TotalRebuildCpu.TotalMilliseconds,11:F0} {result.PublishCount,10} {result.WorstCaseStaleness.TotalMilliseconds,13:F0} {overhead,10:F2}");
    }

    private static double MeasureOneShotRebuildMs(int documentCount, int seed)
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var entries = SyntheticPathFeed.CreateDictionary(documentCount, seed);

        var started = Stopwatch.GetTimestamp();
        updater.RebuildFrom(entries);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
