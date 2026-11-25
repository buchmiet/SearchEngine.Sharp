using BenchmarkDotNet.Attributes;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// End-to-end <see cref="SearchEngineSharp.Find(string, WordMatchMethod, bool, SearchSortMode, FacetFilter?)"/> with
/// the facet filter of <see cref="SelectivityCorpus"/>, by density of the text hits, on a snapshot whose sort
/// permutation is already built. The engine picks the facet and sort strategies itself.
/// </summary>
/// <remarks>
/// The Within query reaches the facet strategies through the bitset pipeline; an Exact query in snapshot order takes
/// a path without them. The Exact query with natural sort goes through both choices: facet strategy on the text hits,
/// sort strategy on the hits left after the filter (about half).
/// </remarks>
[MemoryDiagnoser]
[WarmupCount(2)]
[IterationCount(10)]
public class SelectivityPipelineBenchmark
{
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
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(corpus.Entries);
        _ = provider.Current.GetSortedPermutation();
        _engine = new SearchEngineSharp(provider);
        _filter = corpus.Filter;
        _word = SelectivityCorpus.WordFor(Density);
    }

    [Benchmark(Description = "Within + filter, snapshot order")]
    public int WithinFilterSnapshotOrder()
        => _engine.Find(_word, WordMatchMethod.Within, enableOperators: false, SearchSortMode.SnapshotOrder, _filter).Count;

    [Benchmark(Description = "Exact + filter, natural sort (permutation built)")]
    public int ExactFilterNaturalSort()
        => _engine.Find(_word, WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending, _filter).Count;
}
