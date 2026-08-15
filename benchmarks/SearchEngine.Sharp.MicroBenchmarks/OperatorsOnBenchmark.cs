using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
public class OperatorsOnBenchmark
{
    private SearchEngineSharp _engine = null!;
    private FacetFilter _filter = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        const int seed = 1337;
        var scenario = new BenchScenario("medium", DocumentCount: 100_000, VocabularySize: 30_000, QueryCount: 500);
        var data = SyntheticDataFactory.CreateWithFacets(scenario, seed);
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(data.Documents.ToDictionary(
            d => d.Id,
            d => new IndexedEntry(
                d.Text,
                d.Text,
                FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = d.SizeBytes,
                    ["modified"] = d.ModifiedTicks,
                }))));
        _engine = new SearchEngineSharp(provider);
        _filter = FacetFilter.Range("size", 1_024, 1_048_576);
        _query = data.ExactQueries[0];
    }

    [Benchmark(Baseline = true)]
    public int Exact_OperatorsOff()
    {
        var results = _engine.Find(_query, WordMatchMethod.Exact, enableOperators: false);
        return results.Count;
    }

    [Benchmark]
    public int Exact_OperatorsOn()
    {
        var results = _engine.Find(_query, WordMatchMethod.Exact, enableOperators: true);
        return results.Count;
    }

    [Benchmark]
    public int ExactFacet_OperatorsOff()
    {
        var results = _engine.Find(_query, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, _filter);
        return results.Count;
    }

    [Benchmark]
    public int ExactFacet_OperatorsOn()
    {
        var results = _engine.Find(_query, WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, _filter);
        return results.Count;
    }

    [Benchmark]
    public int CountExact_OperatorsOff()
        => _engine.CountMatches(_query, WordMatchMethod.Exact, enableOperators: false);

    [Benchmark]
    public int CountExact_OperatorsOn()
        => _engine.CountMatches(_query, WordMatchMethod.Exact, enableOperators: true);

    [Benchmark]
    public int CountExactFacet_OperatorsOff()
        => _engine.CountMatches(_query, WordMatchMethod.Exact, false, _filter);

    [Benchmark]
    public int CountExactFacet_OperatorsOn()
        => _engine.CountMatches(_query, WordMatchMethod.Exact, true, _filter);
}
