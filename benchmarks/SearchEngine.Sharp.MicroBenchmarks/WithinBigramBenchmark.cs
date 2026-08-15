using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
public class WithinBigramBenchmark
{
    private IndexSnapshot _snapshot = null!;
    private string _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = FileSearchDataFactory.Create(100_000, seed: 2026);
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(FileSearchDataFactory.ToIndexedEntries(data.Documents));
        _snapshot = provider.Current;
        _query = data.InfixQueries[1]; // "tion" — repeated bigrams in file names
    }

    [Benchmark(Baseline = true, Description = "First bigram only (legacy)")]
    public int Within_FirstBigram()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        return QueryMatcher.MatchWithin(_query, qc, _snapshot, useRarestBigram: false).GetTrueCount();
    }

    [Benchmark(Description = "Rarest bigram among query bigrams")]
    public int Within_RarestBigram()
    {
        using var qc = new QueryContext(_snapshot.DocumentCount);
        return QueryMatcher.MatchWithin(_query, qc, _snapshot, useRarestBigram: true).GetTrueCount();
    }
}
