using BenchmarkDotNet.Attributes;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The natural sort strategies of <see cref="ResultMaterializer"/> on a new snapshot (cold): the full permutation
/// strategy first builds the permutation of all documents. One call per iteration, each on a new snapshot.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(10)]
[InvocationCount(1)]
public class NaturalSortColdSelectivityBenchmark
{
    private const int DocumentCount = 100_000;

    private Dictionary<int, IndexedEntry> _entries = null!;
    private IndexSnapshot _snapshot = null!;
    private FastBitSet _hits = null!;
    private int _hitCount;

    public static IEnumerable<double> Densities => SelectivityCorpus.Densities;

    [ParamsSource(nameof(Densities))]
    public double Density { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _entries = SelectivityCorpus.Create(DocumentCount).Entries;
        _hitCount = HitSets.CountFor(Density, DocumentCount);
        _hits = HitSets.Random(DocumentCount, _hitCount, seed: 1);
    }

    [IterationSetup]
    public void BuildNewSnapshot()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _snapshot = provider.Current;
    }

    [Benchmark(Baseline = true, Description = "Full permutation (cold): build it, walk it, keep the hits")]
    public int FullPermutation()
        => ResultMaterializer.MaterializeNaturalSortFullPermutation(_snapshot, _hits, _hitCount).Count;

    [Benchmark(Description = "K only: sort the hits")]
    public int SortHitsOnly()
        => ResultMaterializer.MaterializeNaturalSortKOnly(_snapshot, _hits, _hitCount).Count;
}
