using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
public class NaturalSortBenchmark
{
    private Dictionary<int, IndexedEntry> _entries = null!;
    private SearchEngineSharp _coldEngine = null!;
    private SearchEngineSharp _warmEngine = null!;
    private SearchEngineSharp _snapshotOrderEngine = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(100_000, seed: 2026);
        _entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _query = data.InfixQueries[0];
        RebuildSnapshotOrderEngine();
    }

    [IterationSetup(Targets = [nameof(NaturalSort_Cold)])]
    public void ColdIterationSetup()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _coldEngine = new SearchEngineSharp(provider);
    }

    [IterationSetup(Targets = [nameof(NaturalSort_Warm)])]
    public void WarmIterationSetup()
    {
        ColdIterationSetup();
        _ = _coldEngine.Find(
            _query,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending);
        _warmEngine = _coldEngine;
    }

    [Benchmark(Baseline = true, Description = "Cold — fresh snapshot, first NaturalSort query")]
    public int NaturalSort_Cold()
        => _coldEngine.Find(
            _query,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "Warm — same snapshot, permutation already cached")]
    public int NaturalSort_Warm()
        => _warmEngine.Find(
            _query,
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending).Count;

    [Benchmark(Description = "SnapshotOrder baseline (no sort build)")]
    public int SnapshotOrder()
        => _snapshotOrderEngine.Find(
            _query,
            WordMatchMethod.Within,
            enableOperators: true).Count;

    private void RebuildSnapshotOrderEngine()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries);
        _snapshotOrderEngine = new SearchEngineSharp(provider);
    }
}
