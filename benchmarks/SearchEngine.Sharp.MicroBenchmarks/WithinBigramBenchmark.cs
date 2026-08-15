using BenchmarkDotNet.Attributes;
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
        _query = data.InfixQueries[1]; // "tion"
    }

    [Benchmark(Baseline = true, Description = "Production-style first bigram (legacy direct path)")]
    public int Within_FirstBigram_Legacy()
        => WithinBigramQueryMatcher.MatchWithinFirstBigram(_query, _snapshot).GetTrueCount();

    [Benchmark(Description = "Experimental rarest bigram among query bigrams")]
    public int Within_RarestBigram_Experimental()
        => WithinBigramQueryMatcher.MatchWithinRarestBigram(_query, _snapshot).GetTrueCount();
}
