using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class FacetSelectivityBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FacetFilter _filter = null!;
    private FastBitSet _textHits = null!;
    private int[] _ordinalBuffer = null!;

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
        _textHits = SelectivityProbe.CreateSparseBitSet(documentCount, HitCount);
        _ordinalBuffer = new int[documentCount];
    }

    [Benchmark(Baseline = true, Description = "Current — facet scan all N ordinals")]
    public int FacetFullScan()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        SelectivityProbe.FacetApplyFullScan(results, _filter, _snapshot, qc);
        return results.GetTrueCount();
    }

    [Benchmark(Description = "Proposed — facet only on K text hits")]
    public int FacetOnHitsOnly()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        SelectivityProbe.FacetApplyOnHitsOnly(results, _filter, _snapshot, qc, _ordinalBuffer);
        return results.GetTrueCount();
    }
}
