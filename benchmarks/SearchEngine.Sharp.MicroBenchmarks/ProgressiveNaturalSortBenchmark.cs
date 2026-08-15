using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Simulates growth-aware progressive ingestion (7 snapshots @ 100k) with NaturalSort requery after each publish.
/// <see cref="IterationSetup"/> rebuilds fresh snapshots so each measured invocation hits a cold NaturalSort cache.
/// </summary>
[MemoryDiagnoser]
public class ProgressiveNaturalSortBenchmark
{
    private static readonly int[] PublishSizes = [2_000, 4_000, 8_000, 16_000, 32_000, 64_000, 100_000];

    private Dictionary<int, IndexedEntry> _allEntries = null!;
    private SearchEngineSharp[] _engines = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(100_000, seed: 2026);
        _allEntries = FileSearchDataFactory.ToIndexedEntries(data.Documents);
        _query = data.InfixQueries[0];
        _engines = new SearchEngineSharp[PublishSizes.Length];
    }

    [IterationSetup]
    public void IterationSetup()
    {
        for (int p = 0; p < PublishSizes.Length; p++)
        {
            var provider = new IndexSnapshotProvider();
            var updater = new IndexUpdater(provider);
            var batch = _allEntries
                .Where(kv => kv.Key < PublishSizes[p])
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            updater.RebuildFrom(batch);
            _engines[p] = new SearchEngineSharp(provider);
        }
    }

    [Benchmark(Baseline = true, Description = "7 cold NaturalSort requeries (fresh snapshot each publish)")]
    public int SevenSnapshot_NaturalSortRequery()
    {
        int total = 0;
        foreach (var engine in _engines)
        {
            total += engine.Find(
                _query,
                WordMatchMethod.Within,
                enableOperators: true,
                SearchSortMode.NaturalSortAscending).Count;
        }

        return total;
    }

    [Benchmark(Description = "7 SnapshotOrder requeries (fresh snapshots, no sort build)")]
    public int SevenSnapshot_SnapshotOrderRequery()
    {
        int total = 0;
        foreach (var engine in _engines)
        {
            total += engine.Find(
                _query,
                WordMatchMethod.Within,
                enableOperators: true).Count;
        }

        return total;
    }
}
