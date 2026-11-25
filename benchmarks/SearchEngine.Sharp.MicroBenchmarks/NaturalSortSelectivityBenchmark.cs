using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The two natural sort strategies of <see cref="ResultMaterializer"/> by number of hits, on a snapshot whose sort
/// permutation is not built yet. The point where they cross is the basis of
/// <see cref="SelectivityThresholds.NaturalSortSortKMaxFraction"/>.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class NaturalSortSelectivityBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private IndexSnapshot _coldSnapshot = null!;
    private FastBitSet _bitSet = null!;

    [Params(0, 1, 10, 100, 1_000, 2_000, 5_000, 10_000, 15_000, 20_000, 30_000, 40_000, 50_000, 75_000, 100_000)]
    public int HitCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var data = FileSearchDataFactory.Create(documentCount, seed: 2026);
        _entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _bitSet = HitSets.Sparse(documentCount, HitCount);
        RebuildColdSnapshot();
    }

    [IterationSetup]
    public void IterationSetup()
        => RebuildColdSnapshot();

    [Benchmark(Baseline = true, Description = "Build the permutation of all documents, scan it")]
    public int FullPermutation()
        => ResultMaterializer.MaterializeNaturalSortFullPermutation(
            _coldSnapshot, _bitSet, HitCount, _coldSnapshot.RecordIds).Count;

    [Benchmark(Description = "Sort the hits only")]
    public int SortHitsOnly()
        => ResultMaterializer.MaterializeNaturalSortKOnly(
            _coldSnapshot, _bitSet, HitCount, _coldSnapshot.RecordIds).Count;

    private void RebuildColdSnapshot()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _coldSnapshot = provider.Current;
    }
}
