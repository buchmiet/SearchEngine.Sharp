using BenchmarkDotNet.Attributes;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Each facet filter strategy of <see cref="FacetFilterEvaluator"/> on its own, by density of the text hits. Where
/// the two cross is what <see cref="SelectivityThresholds.FacetOnHitsMaxFraction"/> should be.
/// </summary>
/// <remarks>
/// Both methods start from a pooled copy of the same random hit set, as the query pipeline does, and apply the
/// filter of <see cref="SelectivityCorpus"/>, which passes half of the documents.
/// </remarks>
[MemoryDiagnoser]
[WarmupCount(2)]
[IterationCount(10)]
public class FacetSelectivityBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private FacetFilter _filter = null!;
    private FastBitSet _textHits = null!;
    private int _hitCount;

    public static IEnumerable<double> Densities => SelectivityCorpus.Densities;

    [ParamsSource(nameof(Densities))]
    public double Density { get; set; }

    [Params(100_000, 1_000_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var corpus = SelectivityCorpus.Create(DocumentCount);
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(corpus.Entries);
        _snapshot = provider.Current;
        _filter = corpus.Filter;
        _hitCount = HitSets.CountFor(Density, DocumentCount);
        _textHits = HitSets.Random(DocumentCount, _hitCount, seed: 1);
    }

    [Benchmark(Baseline = true, Description = "Full scan: test every document, then AND")]
    public int FullScan()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        FacetFilterEvaluator.ApplyFullScan(results, _filter, _snapshot, qc);
        return results.GetTrueCount();
    }

    [Benchmark(Description = "On hits: test the text hits only")]
    public int OnHits()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        var results = qc.RentCopyOf(_textHits);
        FacetFilterEvaluator.ApplyOnHitsInPlace(results, _filter, _snapshot, _hitCount);
        return results.GetTrueCount();
    }
}
