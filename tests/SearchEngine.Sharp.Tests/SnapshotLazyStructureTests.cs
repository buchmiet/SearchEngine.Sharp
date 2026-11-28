using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// The bigram index (Within) and the natural sort permutation (NaturalSortAscending) of a segment are built on first use
// (IndexSegment, LazyInitializer). Concurrent first callers on a new snapshot must share one build instead of each
// building its own copy: a server that gets a burst of queries right after a publish would otherwise build and allocate
// the structure once per request. That the structures carry over to the next snapshot after a small change is tested in
// IndexUpdaterSegmentTests.
public class SnapshotLazyStructureTests
{
    private const int Callers = 8;

    [Test]
    public async Task Concurrent_first_Within_queries_build_the_bigram_index_once()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] counts = await RunConcurrently(() => engine.Find("ab", WordMatchMethod.Within).Count);

        await Assert.That(snapshot.Segments[0].BigramBuildCount).IsEqualTo(1);
        foreach (int count in counts)
            await Assert.That(count).IsEqualTo(counts[0]);
    }

    [Test]
    public async Task Concurrent_first_natural_sort_queries_build_the_permutation_once()
    {
        var (provider, engine) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        int[] firstIds = await RunConcurrently(
            () => engine.Find("*", WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending)[0]);

        await Assert.That(snapshot.Segments[0].SortedPermutationBuildCount).IsEqualTo(1);
        foreach (int id in firstIds)
            await Assert.That(id).IsEqualTo(firstIds[0]);
    }

    [Test]
    public async Task Later_reads_return_the_structure_already_built()
    {
        var (provider, _) = CreateIndex();
        IndexSnapshot snapshot = provider.Current;

        await Assert.That(snapshot.Segments[0].BigramWordIndices).IsSameReferenceAs(snapshot.Segments[0].BigramWordIndices);
        await Assert.That(snapshot.Segments[0].GetSortedPermutation()).IsSameReferenceAs(snapshot.Segments[0].GetSortedPermutation());
        await Assert.That(snapshot.Segments[0].BigramBuildCount).IsEqualTo(1);
        await Assert.That(snapshot.Segments[0].SortedPermutationBuildCount).IsEqualTo(1);
    }

    // 30,000 distinct names: every build takes milliseconds, so callers released together
    // by the barrier overlap with the first build.
    private static (IndexSnapshotProvider Provider, SearchEngineSharp Engine) CreateIndex()
    {
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 30_000; id++)
            entries[id] = $"report-{id * 7919 % 30_000}-ab{id % 97}.pdf";

        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(entries);
        return (provider, engine);
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
