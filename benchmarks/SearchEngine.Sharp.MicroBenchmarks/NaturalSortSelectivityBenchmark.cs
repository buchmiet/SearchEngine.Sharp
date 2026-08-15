using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class NaturalSortSelectivityBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private IndexSnapshot _coldSnapshot = null!;
    private FastBitSet _bitSet = null!;
    private List<int> _results = null!;
    private int[] _ordinalBuffer = null!;

    [Params(0, 1, 10, 100, 1_000, 10_000, 50_000, 100_000)]
    public int HitCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var data = FileSearchDataFactory.Create(documentCount, seed: 2026);
        _entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _bitSet = SelectivityProbe.CreateSparseBitSet(documentCount, HitCount);
        _results = new List<int>(Math.Max(HitCount, 16));
        _ordinalBuffer = new int[documentCount];
        RebuildColdSnapshot();
    }

    [IterationSetup]
    public void IterationSetup()
        => RebuildColdSnapshot();

    [Benchmark(Baseline = true, Description = "Current — build full N permutation, scan all")]
    public int NaturalSortFullPermutationScan()
        => SelectivityProbe.NaturalSortCurrent(_coldSnapshot, _bitSet, _results);

    [Benchmark(Description = "Proposed — enumerate K hits, sort K only")]
    public int NaturalSortKHitsOnly()
        => SelectivityProbe.NaturalSortKOnly(_coldSnapshot, _bitSet, _ordinalBuffer, _results);

    private void RebuildColdSnapshot()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _coldSnapshot = provider.Current;
    }
}
