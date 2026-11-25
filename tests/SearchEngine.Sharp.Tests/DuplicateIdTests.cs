using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// An id names one document. The builder rejects a sequence that repeats an id; the updater's batch methods store
// entries by id, so the last value for an id wins.
public class DuplicateIdTests
{
    [Fact]
    public void Build_rejects_a_repeated_id_in_string_entries()
    {
        KeyValuePair<int, string>[] entries = [new(1, "alpha"), new(2, "beta"), new(1, "gamma")];

        var exception = Assert.Throws<ArgumentException>(() => IndexSnapshotBuilder.Build(entries));

        Assert.Equal("entries", exception.ParamName);
        Assert.Contains("1", exception.Message);
    }

    [Fact]
    public void Build_rejects_a_repeated_id_in_indexed_entries()
    {
        IEnumerable<KeyValuePair<int, IndexedEntry>> Entries()
        {
            yield return new(7, new IndexedEntry("alpha", "alpha"));
            yield return new(42, new IndexedEntry("beta", "beta"));
            yield return new(42, new IndexedEntry("gamma", "gamma"));
        }

        var exception = Assert.Throws<ArgumentException>(
            () => IndexSnapshotBuilder.Build(Entries(), SearchTokenization.FileMask, progress: null));

        Assert.Equal("entries", exception.ParamName);
        Assert.Contains("42", exception.Message);
    }

    [Fact]
    public void Build_accepts_dictionaries_and_distinct_ids()
    {
        var fromDictionary = IndexSnapshotBuilder.Build(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var fromSequence = IndexSnapshotBuilder.Build([new KeyValuePair<int, string>(3, "alpha"), new(4, "alpha")]);

        Assert.Equal(2, fromDictionary.DocumentCount);
        Assert.Equal(2, fromSequence.DocumentCount);
    }

    [Fact]
    public void AddOrUpdateEntries_keeps_the_last_value_of_a_repeated_id()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);

        updater.AddOrUpdateEntries([new KeyValuePair<int, string>(5, "first"), new(6, "other"), new(5, "second")]);

        Assert.Equal(2, updater.EntryCount);
        Assert.Equal(2, engine.DocumentCount);
        Assert.Empty(engine.Find("first", WordMatchMethod.Exact));
        Assert.Equal([5], engine.Find("second", WordMatchMethod.Exact));
    }

    [Fact]
    public void AddOrUpdateEntries_with_indexed_entries_keeps_the_last_value_of_a_repeated_id()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);

        updater.AddOrUpdateEntries(
        [
            new KeyValuePair<int, IndexedEntry>(5, new IndexedEntry("first", "first")),
            new(5, new IndexedEntry("second", "second")),
        ]);

        Assert.Equal(1, engine.DocumentCount);
        Assert.Equal([5], engine.Find("second", WordMatchMethod.Exact));
        Assert.Empty(engine.Find("first", WordMatchMethod.Exact));
    }

    [Fact]
    public async Task RebuildFromAsync_keeps_the_last_value_of_a_repeated_id()
    {
        static async IAsyncEnumerable<KeyValuePair<int, string>> Entries()
        {
            yield return new(9, "first");
            await Task.Yield();
            yield return new(9, "second");
        }

        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);

        await updater.RebuildFromAsync(Entries());

        Assert.Equal(1, engine.DocumentCount);
        Assert.Equal([9], engine.Find("second", WordMatchMethod.Exact));
    }
}
