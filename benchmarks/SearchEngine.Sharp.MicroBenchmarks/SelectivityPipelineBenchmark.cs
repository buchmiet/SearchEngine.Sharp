using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
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
        RebuildColdEngine();
    }

    [IterationSetup]
    public void IterationSetup()
        => RebuildColdEngine();

    [Benchmark(Baseline = true, Description = "0 hits — NaturalSort cold (full pipeline)")]
    public int ZeroHits_NaturalSort()
        => _coldEngine.Find(
            _zeroHitQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "~20 hits — Within+Facet+NaturalSort cold")]
    public int WithinFacet_NaturalSort()
        => _coldEngine.Find(
            _withinQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending,
            _filter).Count;

    [Benchmark(Description = "~20 hits — Within+Facet SnapshotOrder")]
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
