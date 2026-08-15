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
public class BitSetMaterializationBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FastBitSet _bitSet = null!;
    private List<int> _results = null!;
    private int[] _ordinalBuffer = null!;

    [Params(0, 1, 10, 100, 1_000, 10_000, 50_000, 100_000)]
    public int HitCount { get; set; }

    [Params(100_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(DocumentCount, seed: 2026);
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(FileSearchDataFactory.ToIndexedEntries(data.Documents));
        _snapshot = provider.Current;
        _bitSet = SelectivityProbe.CreateSparseBitSet(DocumentCount, HitCount);
        _results = new List<int>(Math.Max(HitCount, 16));
        _ordinalBuffer = new int[DocumentCount];
    }

    [Benchmark(Baseline = true, Description = "Current — scan all N ordinals with Get()")]
    public int ScanAllOrdinals()
        => SelectivityProbe.MaterializeSnapshotOrder(_snapshot, _bitSet, _results);

    [Benchmark(Description = "Proposed — enumerate set bits then materialize K")]
    public int EnumerateSetBits()
        => SelectivityProbe.MaterializeEnumerate(_snapshot, _bitSet, _ordinalBuffer, _results);
}
