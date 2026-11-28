using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Cost of turning a result set into a list of ids in snapshot order, by number of hits.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class BitSetMaterializationBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FastBitSet _bitSet = null!;

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
        _bitSet = HitSets.Random(DocumentCount, HitCount, seed: 1);
    }

    [Benchmark(Description = "ResultMaterializer, snapshot order")]
    public int MaterializeSnapshotOrder()
        => ResultMaterializer.Materialize(_snapshot, _bitSet, SearchSortMode.SnapshotOrder).Count;
}
