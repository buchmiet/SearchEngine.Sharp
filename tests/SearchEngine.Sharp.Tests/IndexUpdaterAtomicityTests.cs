using System.Reflection;
using System.Runtime.CompilerServices;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

// IndexUpdater mutations are atomic: when building the new snapshot throws, the stored entries and the published
// snapshot stay as they were, so the index never answers from entries it does not hold. The batch methods read their
// whole input before they change anything, so an input that throws while it is read, even after it called the updater
// itself, leaves the index as it was. Each mutating method is its own path in src/IndexUpdater.cs; the snapshot build
// is made to fail with a facet bag that has a null name.
public class IndexUpdaterAtomicityTests
{
    // FacetValues validates names on creation, so a bag with a null name can only be forced into existence; it makes
    // the snapshot build throw, like any other component that fails during Build. It is built once, outside the act,
    // and a missing field fails every test here instead of throwing inside the act, where it would pass for the
    // failure under test.
    private static readonly IndexedEntry Poisoned = CreatePoisoned();

    private static IndexedEntry CreatePoisoned()
    {
        var bag = (FacetValues)RuntimeHelpers.GetUninitializedObject(typeof(FacetValues));
        SetField(bag, "_names", new string[] { null! });
        SetField(bag, "_values", new long[] { 1 });
        return new IndexedEntry("poisoned", "poisoned", bag);

        static void SetField(FacetValues bag, string name, object value)
            => (typeof(FacetValues).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"FacetValues has no field {name}."))
                .SetValue(bag, value);
    }

    private static IEnumerable<KeyValuePair<int, string>> ThrowingAfterFirst()
    {
        yield return new(100, "applied-before-failure");
        throw new InvalidOperationException("enumeration failed");
    }

    [Test]
    public async Task AddEntry_that_fails_to_build_keeps_the_entries_and_the_published_snapshot()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.AddEntry(3, Poisoned)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(updater.ContainsEntry(3)).IsFalse();
        await Assert.That(provider.Current).IsSameReferenceAs(published);

        updater.AddEntry(4, "gamma");
        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(TestIndex.Ids(engine, "gamma")).IsEquivalentTo([4], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddEntry_that_fails_to_replace_an_entry_keeps_the_previous_entry()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });

        await Assert.That(() => updater.AddEntry(1, Poisoned)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        updater.AddEntry(3, "gamma");
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "gamma")).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddOrUpdateEntries_that_fails_to_build_rolls_back_the_whole_batch()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.AddOrUpdateEntries(new Dictionary<int, IndexedEntry>
        {
            [2] = new("replaced", "replaced"),
            [3] = new("added", "added"),
            [4] = Poisoned,
        })).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(provider.Current).IsSameReferenceAs(published);
        updater.AddEntry(5, "gamma");
        await Assert.That(TestIndex.Ids(engine, "beta")).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "replaced")).IsEmpty();
        await Assert.That(TestIndex.Ids(engine, "added")).IsEmpty();
    }

    [Test]
    public async Task AddOrUpdateEntries_whose_input_throws_while_read_changes_nothing()
    {
        var (_, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        await Assert.That(() => updater.AddOrUpdateEntries(ThrowingAfterFirst())).ThrowsExactly<InvalidOperationException>();

        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(updater.ContainsEntry(100)).IsFalse();
        await Assert.That(provider.Current).IsSameReferenceAs(published);
    }

    // The caller's sequence changes the index while it is read (on the same thread) and then fails. The nested
    // change stands on its own; the failed batch leaves no trace, neither in the entries nor in the published index.
    [Test]
    public async Task A_batch_whose_input_changes_the_updater_and_then_throws_leaves_entries_and_snapshot_in_agreement()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "old", [2] = "two", [3] = "three" });

        IEnumerable<KeyValuePair<int, string>> Renaming()
        {
            yield return new(1, "renamed");
            updater.RemoveEntry(2);
            throw new IOException("scan failed");
        }

        IEnumerable<int> Removing()
        {
            yield return 1;
            updater.AddEntry(4, "four");
            throw new IOException("scan failed");
        }

        await Assert.That(() => updater.AddOrUpdateEntries(Renaming())).ThrowsExactly<IOException>();
        await Assert.That(() => updater.RemoveEntries(Removing())).ThrowsExactly<IOException>();

        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(updater.ContainsEntry(2)).IsFalse();
        await Assert.That(TestIndex.Ids(engine, "renamed")).IsEmpty();
        await Assert.That(TestIndex.Ids(engine, "two")).IsEmpty();
        await Assert.That(TestIndex.Ids(engine, "old")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "three")).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "four")).IsEquivalentTo([4], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RefreshEntry_that_fails_to_build_keeps_the_previous_entry()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });

        await Assert.That(() => updater.RefreshEntry(1, Poisoned)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => updater.RefreshEntry(2, Poisoned)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(updater.ContainsEntry(2)).IsFalse();
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFrom_that_fails_to_build_keeps_the_previous_entries()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.RebuildFrom(new Dictionary<int, IndexedEntry> { [9] = Poisoned })).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(updater.ContainsEntry(1)).IsTrue();
        await Assert.That(provider.Current).IsSameReferenceAs(published);
        updater.AddEntry(3, "gamma");
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFromAsync_that_fails_to_build_keeps_the_previous_entries()
    {
        var (_, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        await Assert.That(() => updater.RebuildFromAsync(PoisonedStream())).ThrowsExactly<ArgumentNullException>();

        await Assert.That(updater.ContainsEntry(1)).IsTrue();
        await Assert.That(updater.ContainsEntry(9)).IsFalse();
        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(provider.Current).IsSameReferenceAs(published);

        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> PoisonedStream()
        {
            await Task.Yield();
            yield return new(9, Poisoned);
        }
    }
}
