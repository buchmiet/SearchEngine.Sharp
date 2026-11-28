using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// An id names one document. The builder rejects a sequence that repeats an id; the updater's batch methods store
// entries by id, so the last value for an id wins.
public class DuplicateIdTests
{
    [Test]
    public async Task Build_rejects_a_repeated_id_in_string_entries()
    {
        KeyValuePair<int, string>[] entries = [new(1, "alpha"), new(2, "beta"), new(1, "gamma")];

        var exception = await Assert.That(() => IndexSnapshotBuilder.Build(entries)).ThrowsExactly<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("entries");
        await Assert.That(exception.Message).Contains("1", StringComparison.Ordinal);
    }

    [Test]
    public async Task Build_rejects_a_repeated_id_in_indexed_entries()
    {
        IEnumerable<KeyValuePair<int, IndexedEntry>> Entries()
        {
            yield return new(7, new IndexedEntry("alpha", "alpha"));
            yield return new(42, new IndexedEntry("beta", "beta"));
            yield return new(42, new IndexedEntry("gamma", "gamma"));
        }

        var exception = await Assert.That(
            () => IndexSnapshotBuilder.Build(Entries(), SearchTokenization.FileMask, progress: null)).ThrowsExactly<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("entries");
        await Assert.That(exception.Message).Contains("42", StringComparison.Ordinal);
    }

    [Test]
    public async Task Build_accepts_dictionaries_and_distinct_ids()
    {
        var fromDictionary = IndexSnapshotBuilder.Build(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var fromSequence = IndexSnapshotBuilder.Build([new KeyValuePair<int, string>(3, "alpha"), new(4, "alpha")]);

        await Assert.That(fromDictionary.DocumentCount).IsEqualTo(2);
        await Assert.That(fromSequence.DocumentCount).IsEqualTo(2);
    }

    [Test]
    public async Task AddOrUpdateEntries_keeps_the_last_value_of_a_repeated_id()
    {
        var (engine, updater, _) = TestIndex.Create();

        updater.AddOrUpdateEntries([new KeyValuePair<int, string>(5, "first"), new(6, "other"), new(5, "second")]);

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(engine.DocumentCount).IsEqualTo(2);
        await Assert.That(engine.Find("first", WordMatchMethod.Exact)).IsEmpty();
        await Assert.That(engine.Find("second", WordMatchMethod.Exact)).IsEquivalentTo([5], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddOrUpdateEntries_with_indexed_entries_keeps_the_last_value_of_a_repeated_id()
    {
        var (engine, updater, _) = TestIndex.Create();

        updater.AddOrUpdateEntries(
        [
            new KeyValuePair<int, IndexedEntry>(5, new IndexedEntry("first", "first")),
            new(5, new IndexedEntry("second", "second")),
        ]);

        await Assert.That(engine.DocumentCount).IsEqualTo(1);
        await Assert.That(engine.Find("second", WordMatchMethod.Exact)).IsEquivalentTo([5], CollectionOrdering.Matching);
        await Assert.That(engine.Find("first", WordMatchMethod.Exact)).IsEmpty();
    }

    [Test]
    public async Task RebuildFromAsync_keeps_the_last_value_of_a_repeated_id()
    {
        static async IAsyncEnumerable<KeyValuePair<int, string>> Entries()
        {
            yield return new(9, "first");
            await Task.Yield();
            yield return new(9, "second");
        }

        var (engine, updater, _) = TestIndex.Create();

        await updater.RebuildFromAsync(Entries());

        await Assert.That(engine.DocumentCount).IsEqualTo(1);
        await Assert.That(engine.Find("second", WordMatchMethod.Exact)).IsEquivalentTo([9], CollectionOrdering.Matching);
    }
}
