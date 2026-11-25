using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class SnapshotBuildAllocationBenchmark
{
    private Dictionary<int, IndexedEntry> _entries100k = null!;
    private Dictionary<int, IndexedEntry> _entries250k = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries100k = FileSearchDataFactory.ToIndexedEntries(
            FileSearchDataFactory.Create(100_000, seed: 2026).Documents);
        _entries250k = FileSearchDataFactory.ToIndexedEntries(
            FileSearchDataFactory.Create(250_000, seed: 2026).Documents);
    }

    [Benchmark(Baseline = true, Description = "Full rebuild of 100k documents")]
    public int Rebuild_100k()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries100k);
        return provider.Current.DocumentCount;
    }

    [Benchmark(Description = "Full rebuild of 250k documents")]
    public int Rebuild_250k()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries250k);
        return provider.Current.DocumentCount;
    }
}
