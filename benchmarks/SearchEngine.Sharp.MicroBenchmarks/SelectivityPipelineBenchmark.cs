using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Measured hit counts for the within+facet scenario (file corpus @ 100k, seed 2026).
/// Populated once in <see cref="SelectivityPipelineBenchmark.GlobalSetup"/>.
/// </summary>
internal static class SelectivityPipelineCounts
{
    internal const string WithinQuery = "report";
    internal static int TextHitCount { get; set; }
    internal static int PostFacetHitCount { get; set; }
}

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(5)]
public class SelectivityPipelineBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private SearchEngineSharp _coldEngine = null!;
    private FacetFilter _filter = null!;
    private string _zeroHitQuery = null!;
    private string _withinQuery = null!;

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var data = FileSearchDataFactory.Create(documentCount, seed: 2026);
        _entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _filter = FacetFilter.Range("size", 1_024, 1_048_576);
        _zeroHitQuery = "zzzznotfound999";
        _withinQuery = data.InfixQueries[0];

        var measureProvider = new IndexSnapshotProvider();
        new IndexUpdater(measureProvider).RebuildFrom(_entries);
        var measureEngine = new SearchEngineSharp(measureProvider);
        SelectivityPipelineCounts.TextHitCount = measureEngine.CountMatches(
            _withinQuery, WordMatchMethod.Within, enableOperators: true);
        SelectivityPipelineCounts.PostFacetHitCount = measureEngine.CountMatches(
            _withinQuery, WordMatchMethod.Within, enableOperators: true, _filter);

        Console.WriteLine(
            $"SelectivityPipelineCounts: query='{_withinQuery}' textHits={SelectivityPipelineCounts.TextHitCount} postFacet={SelectivityPipelineCounts.PostFacetHitCount}");

        RebuildColdEngine();
    }

    [IterationSetup]
    public void IterationSetup()
        => RebuildColdEngine();

    [Benchmark(Baseline = true, Description = "0 text hits + NaturalSort cold")]
    public int ZeroHits_NaturalSort()
        => _coldEngine.Find(
            _zeroHitQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "Within+Facet+NaturalSort cold (see SelectivityPipelineCounts)")]
    public int WithinFacet_NaturalSort()
        => _coldEngine.Find(
            _withinQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending,
            _filter).Count;

    [Benchmark(Description = "Within+Facet SnapshotOrder (see SelectivityPipelineCounts)")]
    public int WithinFacet_SnapshotOrder()
        => _coldEngine.Find(
            _withinQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.SnapshotOrder,
            _filter).Count;

    private void RebuildColdEngine()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _coldEngine = new SearchEngineSharp(provider);
    }
}
