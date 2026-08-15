using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Finer K sweep to locate empirical crossover between global permutation vs precomputed sort-K.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class NaturalSortCrossoverBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private IndexSnapshot _coldSnapshot = null!;
    private FastBitSet _bitSet = null!;
    private List<int> _results = null!;
    private int[] _ordinalBuffer = null!;
    private string[] _keyScratch = null!;

    [Params(2_000, 5_000, 10_000, 15_000, 20_000, 30_000, 40_000, 50_000, 75_000, 100_000)]
    public int HitCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var data = FileSearchDataFactory.Create(documentCount, seed: 2026);
        _entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _bitSet = SelectivityProbe.CreateSparseBitSet(documentCount, HitCount);
        _results = new List<int>(HitCount);
        _ordinalBuffer = new int[documentCount];
        _keyScratch = new string[documentCount];
        RebuildColdSnapshot();
    }

    [IterationSetup]
    public void IterationSetup()
        => RebuildColdSnapshot();

    [Benchmark(Baseline = true, Description = "Current — full N cold permutation + scan")]
    public int GlobalPermutation()
        => SelectivityProbe.NaturalSortCurrent(_coldSnapshot, _bitSet, _results);

    [Benchmark(Description = "Sort K — precompute K keys once (production-shaped)")]
    public int SortKPrecomputedKeys()
        => SelectivityProbe.NaturalSortKOnlyPrecomputedKeys(
            _coldSnapshot, _bitSet, _ordinalBuffer, _keyScratch, _results);

    private void RebuildColdSnapshot()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _coldSnapshot = provider.Current;
    }
}
