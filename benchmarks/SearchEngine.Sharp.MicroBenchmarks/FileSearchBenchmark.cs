using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
public class FileSearchBenchmark
{
    private SearchEngineSharp _engine = null!;
    private FacetFilter _filter = null!;
    private string _withinQuery = null!;
    private string _exactQuery = null!;
    private string _globQuery = null!;
    private string _booleanQuery = null!;

    [GlobalSetup]
    public void Setup()
    {
        const int seed = 2026;
        var data = FileSearchDataFactory.Create(100_000, seed);
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(FileSearchDataFactory.ToIndexedEntries(data.Documents));
        _engine = new SearchEngineSharp(provider);
        _filter = FacetFilter.Range("size", 1_024, 1_048_576);
        _withinQuery = data.InfixQueries[0];
        _exactQuery = data.ExactQueries[0];
        _globQuery = data.GlobQueries[0];
        _booleanQuery = data.BooleanQueries[0];
    }

    [Benchmark(Baseline = true)]
    public int Within_OperatorsOn()
        => _engine.Find(_withinQuery, WordMatchMethod.Within, enableOperators: true).Count;

    [Benchmark]
    public int Exact_OperatorsOn()
        => _engine.Find(_exactQuery, WordMatchMethod.Exact, enableOperators: true).Count;

    [Benchmark]
    public int Glob_OperatorsOn()
        => _engine.Find(_globQuery, WordMatchMethod.Exact, enableOperators: true).Count;

    [Benchmark]
    public int Boolean_OperatorsOn()
        => _engine.Find(_booleanQuery, WordMatchMethod.Exact, enableOperators: true).Count;

    [Benchmark]
    public int WithinFacet_NaturalSort()
        => _engine.Find(
            _withinQuery,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending,
            _filter).Count;

    [Benchmark]
    public int FilterOnly_CountMatches()
        => _engine.CountMatches("", WordMatchMethod.Exact, enableOperators: true, _filter);
}
