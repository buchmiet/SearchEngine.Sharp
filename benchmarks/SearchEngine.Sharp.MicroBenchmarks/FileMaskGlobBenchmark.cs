using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class FileMaskGlobBenchmark
{
    private SearchEngineSharp _defaultEngine = null!;
    private SearchEngineSharp _fileMaskEngine = null!;
    private FacetFilter _filter = null!;
    private string _globPdf = null!;
    private string _globJpg = null!;

    [Params(100_000, 250_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(DocumentCount, seed: 2026);
        var entries = FileSearchDataFactory.ToIndexedEntries(data.Documents);

        var defaultProvider = new IndexSnapshotProvider();
        new IndexUpdater(defaultProvider).RebuildFrom(entries);
        _defaultEngine = new SearchEngineSharp(defaultProvider);

        var fileMaskProvider = new IndexSnapshotProvider();
        new IndexUpdater(fileMaskProvider, SearchTokenization.FileMask).RebuildFrom(entries);
        _fileMaskEngine = new SearchEngineSharp(fileMaskProvider);

        _filter = FacetFilter.Range("size", 1_024, 1_048_576);
        _globPdf = "*.pdf";
        _globJpg = "*.jpg";
    }

    [Benchmark(Baseline = true, Description = "Default tokenization — *.pdf (broken: '.' splits query)")]
    public int GlobPdf_DefaultTokenization()
        => _defaultEngine.Find(_globPdf, WordMatchMethod.Exact, enableOperators: true).Count;

    [Benchmark(Description = "FileMask tokenization — whole-filename *.pdf")]
    public int GlobPdf_FileMask()
        => _fileMaskEngine.Find(_globPdf, WordMatchMethod.Exact, enableOperators: true).Count;

    [Benchmark(Description = "FileMask — *.pdf + facet (full N facet scan)")]
    public int GlobPdfFacet_FileMask()
        => _fileMaskEngine.Find(
            _globPdf,
            WordMatchMethod.Exact,
            enableOperators: true,
            SearchSortMode.SnapshotOrder,
            _filter).Count;

    [Benchmark(Description = "FileMask — *.jpg whole filename")]
    public int GlobJpg_FileMask()
        => _fileMaskEngine.Find(_globJpg, WordMatchMethod.Exact, enableOperators: true).Count;
}
