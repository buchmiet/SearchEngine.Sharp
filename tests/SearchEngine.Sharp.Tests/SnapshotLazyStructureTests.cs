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

    [Test]
    public async Task BigramIndex_ConcurrentFirstWithinQueries_BuildItOnce()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] counts = await RunConcurrently(() => engine.Find("ab", WordMatchMethod.Within).Count);

        await Assert.That(snapshot.BigramBuildCount).IsEqualTo(1);
        foreach (int count in counts)
            await Assert.That(count).IsEqualTo(counts[0]);
    }

    [Test]
    public async Task SortedPermutation_ConcurrentFirstNaturalSortQueries_BuildItOnce()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] firstIds = await RunConcurrently(
            () => engine.Find("*", WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending)[0]);

        await Assert.That(snapshot.SortedPermutationBuildCount).IsEqualTo(1);
        foreach (int id in firstIds)
            await Assert.That(id).IsEqualTo(firstIds[0]);
    }

    [Test]
    public async Task LazyStructures_RepeatedReads_ReturnTheSameInstance()
    {
        var (provider, _) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        await Assert.That(snapshot.BigramWordIndices).IsSameReferenceAs(snapshot.BigramWordIndices);
        await Assert.That(snapshot.GetSortedPermutation()).IsSameReferenceAs(snapshot.GetSortedPermutation());
        await Assert.That(snapshot.BigramBuildCount).IsEqualTo(1);
        await Assert.That(snapshot.SortedPermutationBuildCount).IsEqualTo(1);
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
    private static async Task<int[]> RunConcurrently(Func<int> query)
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

        return await Task.WhenAll(callers);
    }
}
