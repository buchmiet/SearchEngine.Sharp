using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// How IndexUpdater lays out the snapshots it publishes: a small change keeps the base of the previous snapshot, and
// the base is rebuilt (a merge) once the delta and the dead ordinals pass IndexUpdater.MergeThreshold. The answers
// themselves are checked against full builds by IndexUpdaterDifferentialTests.
public class IndexUpdaterSegmentTests
{
    private const int EntryCount = 1000;

    private static Dictionary<int, string> Entries()
    {
        var entries = new Dictionary<int, string>(EntryCount);
        for (int id = 0; id < EntryCount; id++)
            entries[id] = $"file-{id}.txt";
        return entries;
    }

    [Test]
    public async Task A_small_change_keeps_the_base_and_its_lazy_structures()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(Entries());
        _ = engine.Find("ile", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);
        var @base = provider.Current.Base;
        await Assert.That(@base.IsSortedPermutationBuilt).IsTrue();

        updater.AddEntry(EntryCount, "file-5a.txt");
        updater.RemoveEntry(7);
        var results = engine.Find("ile", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(provider.Current.Base).IsSameReferenceAs(@base);
        await Assert.That(provider.Current.Delta!.RecordIds).IsEquivalentTo(new[] { EntryCount }, CollectionOrdering.Matching);
        await Assert.That(provider.Current.DeadOrdinals).IsEquivalentTo(new[] { 7 }, CollectionOrdering.Matching);
        await Assert.That(@base.BigramBuildCount).IsEqualTo(1);
        await Assert.That(@base.SortedPermutationBuildCount).IsEqualTo(1);
        await Assert.That(results.Count).IsEqualTo(EntryCount);
        await Assert.That(results.Take(8)).IsEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, EntryCount, 6 }, CollectionOrdering.Matching);
        await Assert.That(results).DoesNotContain(7);
    }

    [Test]
    public async Task The_base_is_rebuilt_once_the_delta_and_the_dead_ordinals_pass_the_threshold()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(Entries());
        var @base = provider.Current.Base;
        int threshold = IndexUpdater.MergeThreshold(EntryCount);

        // Replacing a base entry adds one delta entry and one dead ordinal.
        int id = 0;
        for (; 2 * (id + 1) <= threshold; id++)
            updater.RefreshEntry(id, $"renamed-{id}.txt");

        await Assert.That(provider.Current.Base).IsSameReferenceAs(@base);
        await Assert.That(provider.Current.Delta!.DocumentCount + provider.Current.DeadOrdinals.Length).IsEqualTo(threshold);

        updater.RefreshEntry(id, $"renamed-{id}.txt");

        await Assert.That(provider.Current.Base).IsNotSameReferenceAs(@base);
        await Assert.That(provider.Current.Delta).IsNull();
        await Assert.That(provider.Current.DeadOrdinals).IsEmpty();
        await Assert.That(engine.CountMatches("renamed", WordMatchMethod.Within)).IsEqualTo(id + 1);
        await Assert.That(engine.DocumentCount).IsEqualTo(EntryCount);
    }

    [Test]
    public async Task The_updater_from_dependency_injection_keeps_the_base_of_the_initial_snapshot()
    {
        using var services = new ServiceCollection().AddSearchEngine(Entries()).BuildServiceProvider();
        var provider = services.GetRequiredService<IIndexSnapshotProvider>();
        var updater = services.GetRequiredService<IIndexUpdater>();
        var engine = services.GetRequiredService<ISearchEngine>();
        var initial = provider.Current;
        int ordinalOf42 = Array.IndexOf(initial.Base.RecordIds, 42);

        updater.RefreshEntry(42, "moved.txt");

        await Assert.That(provider.Current.Base).IsSameReferenceAs(initial.Base);
        await Assert.That(provider.Current.DeadOrdinals).IsEquivalentTo(new[] { ordinalOf42 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("moved", WordMatchMethod.Within)).IsEquivalentTo(new[] { 42 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("42", WordMatchMethod.Exact)).IsEmpty();
        await Assert.That(engine.DocumentCount).IsEqualTo(EntryCount);
    }
}
