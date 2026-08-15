using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Simulates growth-aware progressive ingestion (7 snapshots @ 100k) with NaturalSort requery after each publish.
/// </summary>
[MemoryDiagnoser]
public class ProgressiveNaturalSortBenchmark
{
    private SearchEngineSharp[] _engines = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(100_000, seed: 2026);
        _query = data.InfixQueries[0];
        int[] publishSizes = [2_000, 4_000, 8_000, 16_000, 32_000, 64_000, 100_000];
        var entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);

        _engines = new SearchEngineSharp[publishSizes.Length];
        for (int p = 0; p < publishSizes.Length; p++)
        {
            var provider = new IndexSnapshotProvider();
            var updater = new IndexUpdater(provider);
            var batch = entries
                .Where(kv => kv.Key < publishSizes[p])
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            updater.RebuildFrom(batch);
            _engines[p] = new SearchEngineSharp(provider);
        }
    }

    [Benchmark(Baseline = true, Description = "7 cold NaturalSort requeries (one per snapshot)")]
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

    [Benchmark(Description = "7 SnapshotOrder requeries (same snapshots, no sort build)")]
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
