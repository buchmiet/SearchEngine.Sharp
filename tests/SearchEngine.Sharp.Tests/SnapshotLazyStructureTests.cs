using SearchEngine;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

/// <summary>
/// The bigram index and the natural-sort permutation are built on first use. Concurrent first
/// callers on a fresh snapshot must share one build instead of each building its own copy.
/// </summary>
public class SnapshotLazyStructureTests
{
    private const int Callers = 8;

    [Fact]
    public void BigramIndex_ConcurrentFirstWithinQueries_BuildItOnce()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] counts = RunConcurrently(() => engine.Find("ab", WordMatchMethod.Within).Count);

        Assert.Equal(1, snapshot.BigramBuildCount);
        Assert.All(counts, count => Assert.Equal(counts[0], count));
    }

    [Fact]
    public void SortedPermutation_ConcurrentFirstNaturalSortQueries_BuildItOnce()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] firstIds = RunConcurrently(
            () => engine.Find("*", WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending)[0]);

        Assert.Equal(1, snapshot.SortedPermutationBuildCount);
        Assert.All(firstIds, id => Assert.Equal(firstIds[0], id));
    }

    [Fact]
    public void LazyStructures_RepeatedReads_ReturnTheSameInstance()
    {
        var (provider, _) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        Assert.Same(snapshot.BigramWordIndices, snapshot.BigramWordIndices);
        Assert.Same(snapshot.GetSortedPermutation(), snapshot.GetSortedPermutation());
        Assert.Equal(1, snapshot.BigramBuildCount);
        Assert.Equal(1, snapshot.SortedPermutationBuildCount);
    }

    // 30,000 distinct names: every build takes milliseconds, so callers released together
    // by the barrier overlap with the first build.
    private static (IndexSnapshotProvider Provider, SearchEngineSharp Engine) CreateIndex()
    {
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 30_000; id++)
            entries[id] = $"report-{id * 7919 % 30_000}-ab{id % 97}.pdf";

        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider, SearchTokenization.FileMask).RebuildFrom(entries);
        return (provider, new SearchEngineSharp(provider));
    }

    // Each caller gets its own thread (LongRunning), so all of them reach the barrier even when
    // the thread pool is busy with other tests.
    private static int[] RunConcurrently(Func<int> query)
    {
        using var start = new Barrier(Callers);
        var callers = new Task<int>[Callers];
        for (int i = 0; i < Callers; i++)
        {
            callers[i] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return query();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Task.WaitAll(callers);
        return [.. callers.Select(caller => caller.Result)];
    }
}
