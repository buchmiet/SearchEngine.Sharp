using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
public class NaturalSortBenchmark
{
    private SearchEngineSharp _engine = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(100_000, seed: 2026);
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(FileSearchDataFactory.ToIndexedEntries(data.Documents));
        _engine = new SearchEngineSharp(provider);
        _query = data.InfixQueries[0];
    }

    [Benchmark(Baseline = true, Description = "Cold — first NaturalSort on snapshot")]
    public int NaturalSort_Cold()
        => _engine.Find(_query, WordMatchMethod.Within, enableOperators: true, SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "Warm — permutation cached on same snapshot")]
    public int NaturalSort_Warm()
        => _engine.Find(_query, WordMatchMethod.Within, enableOperators: true, SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "SnapshotOrder baseline (no sort build)")]
    public int SnapshotOrder()
        => _engine.Find(_query, WordMatchMethod.Within, enableOperators: true).Count;
}
