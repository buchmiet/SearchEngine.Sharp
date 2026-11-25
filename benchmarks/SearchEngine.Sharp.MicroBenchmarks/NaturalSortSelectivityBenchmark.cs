using BenchmarkDotNet.Attributes;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Each natural sort strategy of <see cref="ResultMaterializer"/> on its own, by density of the hits, on a snapshot
/// whose sort permutation is already built (warm). Together with <see cref="NaturalSortColdSelectivityBenchmark"/>
/// this shows what <see cref="SelectivityThresholds.NaturalSortSortKMaxFraction"/> should be.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(2)]
[IterationCount(10)]
public class NaturalSortSelectivityBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FastBitSet _hits = null!;
    private int _hitCount;

    public static IEnumerable<double> Densities => SelectivityCorpus.Densities;

    [ParamsSource(nameof(Densities))]
    public double Density { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(SelectivityCorpus.Create(documentCount).Entries);
        _snapshot = provider.Current;
        _ = _snapshot.GetSortedPermutation();
        _hitCount = HitSets.CountFor(Density, documentCount);
        _hits = HitSets.Random(documentCount, _hitCount, seed: 1);
    }

    [Benchmark(Baseline = true, Description = "Full permutation (built): walk it, keep the hits")]
    public int FullPermutation()
        => ResultMaterializer.MaterializeNaturalSortFullPermutation(_snapshot, _hits, _hitCount, _snapshot.RecordIds).Count;

    [Benchmark(Description = "K only: sort the hits")]
    public int SortHitsOnly()
        => ResultMaterializer.MaterializeNaturalSortKOnly(_snapshot, _hits, _hitCount, _snapshot.RecordIds).Count;
}
