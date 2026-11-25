using BenchmarkDotNet.Attributes;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The natural sort query of <see cref="SelectivityPipelineBenchmark"/> as the first query on a new snapshot, by
/// density of the text hits. One call per iteration, each on a new snapshot.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(10)]
[InvocationCount(1)]
public class SelectivityPipelineColdBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private SearchEngineSharp _engine = null!;
    private FacetFilter _filter = null!;
    private string _word = null!;

    public static IEnumerable<double> Densities => SelectivityCorpus.Densities;

    [ParamsSource(nameof(Densities))]
    public double Density { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var corpus = SelectivityCorpus.Create(100_000);
        _entries = corpus.Entries;
        _filter = corpus.Filter;
        _word = SelectivityCorpus.WordFor(Density);
    }

    [IterationSetup]
    public void BuildNewSnapshot()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _engine = new SearchEngineSharp(provider);
    }

    [Benchmark(Description = "Exact + filter, natural sort (new snapshot)")]
    public int ExactFilterNaturalSort()
        => _engine.Find(_word, WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending, _filter).Count;
}
