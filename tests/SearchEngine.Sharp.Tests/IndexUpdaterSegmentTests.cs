using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// How IndexUpdater lays out the snapshots it publishes: every change adds one small segment and marks documents dead
// where they are, and neighbouring segments of about the same size are merged (MergePolicy). The answers themselves
// are checked against full builds by IndexUpdaterDifferentialTests.
public class IndexUpdaterSegmentTests
{
    private const int EntryCount = 1000;

    // No background thread: a merge happens only where a test calls MergeNow.
    private static readonly MergePolicy Manual = new() { Background = false };

    private static Dictionary<int, string> Entries()
    {
        var entries = new Dictionary<int, string>(EntryCount);
        for (int id = 0; id < EntryCount; id++)
            entries[id] = $"file-{id}.txt";
        return entries;
    }

    [Test]
    public async Task A_small_change_keeps_the_large_segment_and_its_lazy_structures()
    {
        var (engine, updater, provider) = TestIndex.Create(null, Manual);
        updater.RebuildFrom(Entries());
        _ = engine.Find("ile", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);
        var large = provider.Current.Segments[0];
        await Assert.That(large.IsSortedPermutationBuilt).IsTrue();

        updater.AddEntry(EntryCount, "file-5a.txt");
        updater.RemoveEntry(7);
        var results = engine.Find("ile", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        var snapshot = provider.Current;
        await Assert.That(snapshot.Segments.Length).IsEqualTo(2);
        await Assert.That(snapshot.Segments[0]).IsSameReferenceAs(large);
        await Assert.That(snapshot.Segments[1].RecordIds).IsEquivalentTo(new[] { EntryCount }, CollectionOrdering.Matching);
        await Assert.That(snapshot.Dead[0]!.ToList()).IsEquivalentTo(new[] { 7 }, CollectionOrdering.Matching);
        await Assert.That(large.BigramBuildCount).IsEqualTo(1);
        await Assert.That(large.SortedPermutationBuildCount).IsEqualTo(1);
        await Assert.That(results.Count).IsEqualTo(EntryCount);
        await Assert.That(results.Take(8)).IsEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, EntryCount, 6 }, CollectionOrdering.Matching);
        await Assert.That(results).DoesNotContain(7);
    }

    [Test]
    public async Task A_change_does_not_rebuild_the_index_however_many_changes_came_before()
    {
        var (_, updater, provider) = TestIndex.Create(null, Manual);
        updater.RebuildFrom(Entries());
        var large = provider.Current.Segments[0];

        for (int i = 0; i < 40; i++)
            updater.RefreshEntry(i, $"renamed-{i}.txt");

        await Assert.That(provider.Current.Segments[0]).IsSameReferenceAs(large);
        await Assert.That(provider.Current.Segments.Length).IsEqualTo(41);
        await Assert.That(provider.Current.DocumentCount).IsEqualTo(EntryCount);
    }

    [Test]
    public async Task Segments_of_one_level_merge_into_one_and_keep_their_order()
    {
        var (engine, updater, provider) = TestIndex.Create(null, Manual);
        for (int id = 0; id < 4; id++)
            updater.AddEntry(id, $"item-{id}");
        await Assert.That(updater.SegmentCount).IsEqualTo(4);

        updater.MergeNow();

        await Assert.That(updater.SegmentCount).IsEqualTo(1);
        await Assert.That(engine.Find("item", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("2", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
        await Assert.That(provider.Current.DocumentCount).IsEqualTo(4);
    }

    [Test]
    public async Task A_merge_drops_the_dead_documents_of_its_segments()
    {
        var (engine, updater, provider) = TestIndex.Create(null, Manual);
        for (int id = 0; id < 4; id++)
            updater.AddEntry(id, $"item-{id}");
        updater.RemoveEntry(1);
        updater.RefreshEntry(2, "item-2-new");

        updater.MergeNow();

        var snapshot = provider.Current;
        await Assert.That(snapshot.Dead.All(dead => dead is null)).IsTrue();
        await Assert.That(snapshot.Segments.Sum(segment => segment.DocumentCount)).IsEqualTo(3);
        await Assert.That(engine.Find("item", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 0, 3, 2 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("new", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_segment_that_is_mostly_dead_is_rewritten_without_its_dead_documents()
    {
        var (engine, updater, provider) = TestIndex.Create(null, Manual);
        updater.RebuildFrom(Entries());
        updater.RemoveEntries(Enumerable.Range(0, 400));
        await Assert.That(provider.Current.Dead[0]!.Count).IsEqualTo(400);

        updater.MergeNow();

        await Assert.That(provider.Current.Segments.Length).IsEqualTo(1);
        await Assert.That(provider.Current.Segments[0].DocumentCount).IsEqualTo(EntryCount - 400);
        await Assert.That(provider.Current.Dead[0]).IsNull();
        await Assert.That(engine.CountMatches("file", WordMatchMethod.Exact)).IsEqualTo(EntryCount - 400);
    }

    [Test]
    public async Task A_writer_that_outruns_the_merges_cannot_pile_up_segments()
    {
        var policy = new MergePolicy { Background = false, MaxSegments = 8 };
        var (engine, updater, _) = TestIndex.Create(null, policy);

        for (int id = 0; id < 200; id++)
        {
            updater.AddEntry(id, $"item-{id}");
            await Assert.That(updater.SegmentCount).IsLessThanOrEqualTo(8);
        }

        await Assert.That(engine.CountMatches("item", WordMatchMethod.Exact)).IsEqualTo(200);
    }

    [Test]
    public async Task A_document_removed_while_its_segments_merge_stays_removed_in_the_merged_segment()
    {
        var policy = new MergePolicy { FanOut = 3 };
        var (engine, updater, provider) = TestIndex.Create(null, policy);
        using var built = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        updater.OnMergeBuilt = () =>
        {
            built.Set();
            release.Wait();
        };

        for (int id = 0; id < 3; id++)
            updater.AddEntry(id, $"item-{id}");
        await Assert.That(built.Wait(TimeSpan.FromSeconds(30))).IsTrue();

        // The merge has built its segment but not published it: change what it merged.
        updater.RemoveEntry(1);
        updater.RefreshEntry(2, "other-2");
        updater.AddEntry(3, "item-3");
        updater.OnMergeBuilt = null;
        release.Set();
        updater.WaitForMerges();

        await Assert.That(updater.MergeFault).IsNull();
        await Assert.That(engine.Find("item", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 0, 3 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("other", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
        await Assert.That(engine.DocumentCount).IsEqualTo(3);
        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(provider.Current.Segments.Length).IsLessThan(5);
    }

    [Test]
    public async Task A_merge_that_finishes_after_a_rebuild_is_discarded()
    {
        var policy = new MergePolicy { FanOut = 3 };
        var (engine, updater, _) = TestIndex.Create(null, policy);
        using var built = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        updater.OnMergeBuilt = () =>
        {
            built.Set();
            release.Wait();
        };

        for (int id = 0; id < 3; id++)
            updater.AddEntry(id, $"item-{id}");
        await Assert.That(built.Wait(TimeSpan.FromSeconds(30))).IsTrue();

        updater.RebuildFrom(new Dictionary<int, string> { [10] = "fresh" });
        updater.OnMergeBuilt = null;
        release.Set();
        updater.WaitForMerges();

        await Assert.That(engine.Find("item", WordMatchMethod.Exact)).IsEmpty();
        await Assert.That(engine.Find("fresh", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 10 }, CollectionOrdering.Matching);
        await Assert.That(updater.EntryCount).IsEqualTo(1);
    }

    [Test]
    public async Task Entry_count_and_lookup_follow_the_live_documents()
    {
        var (_, updater, _) = TestIndex.Create(null, Manual);
        updater.RebuildFrom(Entries());
        updater.AddEntry(5000, "new");
        updater.RemoveEntry(3);
        updater.RefreshEntry(4, "again");

        await Assert.That(updater.EntryCount).IsEqualTo(EntryCount);
        await Assert.That(updater.ContainsEntry(3)).IsFalse();
        await Assert.That(updater.ContainsEntry(4)).IsTrue();
        await Assert.That(updater.ContainsEntry(5000)).IsTrue();
        await Assert.That(updater.RemoveEntry(3)).IsFalse();
        await Assert.That(updater.RefreshEntry(9999, "x")).IsFalse();
    }

    [Test]
    public async Task The_updater_from_dependency_injection_keeps_the_segment_of_the_initial_snapshot()
    {
        using var services = new ServiceCollection().AddSearchEngine(Entries()).BuildServiceProvider();
        var provider = services.GetRequiredService<IIndexSnapshotProvider>();
        var updater = services.GetRequiredService<IIndexUpdater>();
        var engine = services.GetRequiredService<ISearchEngine>();
        var initial = provider.Current;
        int localOf42 = Array.IndexOf(initial.Segments[0].RecordIds, 42);

        updater.RefreshEntry(42, "moved.txt");

        await Assert.That(provider.Current.Segments[0]).IsSameReferenceAs(initial.Segments[0]);
        await Assert.That(provider.Current.Dead[0]!.ToList()).IsEquivalentTo(new[] { localOf42 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("moved", WordMatchMethod.Within)).IsEquivalentTo(new[] { 42 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("42", WordMatchMethod.Exact)).IsEmpty();
        await Assert.That(engine.DocumentCount).IsEqualTo(EntryCount);
    }
}
