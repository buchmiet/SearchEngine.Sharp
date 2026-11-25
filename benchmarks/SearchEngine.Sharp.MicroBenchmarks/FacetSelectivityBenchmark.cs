using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The two facet filter strategies of <see cref="FacetFilterEvaluator"/> by number of text hits. The point where
/// they cross is the basis of <see cref="SelectivityThresholds.FacetOnHitsMaxFraction"/>.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class FacetSelectivityBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FacetFilter _filter = null!;
    private FastBitSet _textHits = null!;

    [Params(0, 1, 10, 100, 1_000, 10_000, 50_000, 100_000)]
    public int HitCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int documentCount = 100_000;
        var data = FileSearchDataFactory.Create(documentCount, seed: 2026);
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(FileSearchDataFactory.ToIndexedEntries(data.Documents));
        _snapshot = provider.Current;
        _filter = FacetFilter.Range("size", 1_024, 1_048_576);
        _textHits = HitSets.Sparse(documentCount, HitCount);
    }

    [Benchmark(Baseline = true, Description = "Full scan of all documents")]
    public int FacetFullScan()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        FacetFilterEvaluator.ApplyFullScan(results, _filter, _snapshot, qc);
        return results.GetTrueCount();
    }

    [Benchmark(Description = "Test the text hits only")]
    public int FacetOnHits()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        FacetFilterEvaluator.ApplyOnHitsInPlace(results, _filter, _snapshot, HitCount);
        return results.GetTrueCount();
    }
}
