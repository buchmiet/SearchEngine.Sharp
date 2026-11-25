using System.Reflection;
using System.Runtime.CompilerServices;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class IndexUpdaterTests
{
    private static (SearchEngineSharp engine, IndexUpdater updater, IndexSnapshotProvider provider) Create(SearchTokenization? tokenization = null)
    {
        var provider = new IndexSnapshotProvider();
        return (new SearchEngineSharp(provider), new IndexUpdater(provider, tokenization), provider);
    }

    // FacetValues validates names on creation, so a bag with a null name can only be forced into
    // existence; it makes the snapshot build throw, like any other component that fails during Build.
    private static IndexedEntry Poisoned
    {
        get
        {
            var bag = (FacetValues)RuntimeHelpers.GetUninitializedObject(typeof(FacetValues));
            typeof(FacetValues).GetField("_names", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bag, new string[] { null! });
            typeof(FacetValues).GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bag, new long[] { 1 });
            return new IndexedEntry("poisoned", "poisoned", bag);
        }
    }

    private static IEnumerable<KeyValuePair<int, string>> ThrowingAfterFirst()
    {
        yield return new(100, "applied-before-failure");
        throw new InvalidOperationException("enumeration failed");
    }

    private static int[] Ids(SearchEngineSharp engine, string query)
        => [.. engine.Find(query, WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder).Order()];

    [Fact]
    public void AddEntry_WhenBuildThrows_KeepsStoredEntriesAndPublishedSnapshot()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        Assert.ThrowsAny<Exception>(() => updater.AddEntry(3, Poisoned));

        Assert.Equal(2, updater.EntryCount);
        Assert.False(updater.ContainsEntry(3));
        Assert.Same(published, provider.Current);

        updater.AddEntry(4, "gamma");
        Assert.Equal(3, updater.EntryCount);
        Assert.Equal([4], Ids(engine, "gamma"));
        Assert.Equal([1], Ids(engine, "alpha"));
    }

    [Fact]
    public void AddEntry_WhenReplacingAndBuildThrows_RestoresThePreviousEntry()
    {
        var (engine, updater, _) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });

        Assert.ThrowsAny<Exception>(() => updater.AddEntry(1, Poisoned));

        Assert.Equal(2, updater.EntryCount);
        updater.AddEntry(3, "gamma");
        Assert.Equal([1], Ids(engine, "alpha"));
        Assert.Equal([3], Ids(engine, "gamma"));
    }

    [Fact]
    public void AddOrUpdateEntries_WhenBuildThrows_RollsBackTheWholeBatch()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        Assert.ThrowsAny<Exception>(() => updater.AddOrUpdateEntries(new Dictionary<int, IndexedEntry>
        {
            [2] = new("replaced", "replaced"),
            [3] = new("added", "added"),
            [4] = Poisoned,
        }));

        Assert.Equal(2, updater.EntryCount);
        Assert.Same(published, provider.Current);
        updater.AddEntry(5, "gamma");
        Assert.Equal([2], Ids(engine, "beta"));
        Assert.Empty(Ids(engine, "replaced"));
        Assert.Empty(Ids(engine, "added"));
    }

    [Fact]
    public void AddOrUpdateEntries_WhenTheSourceEnumerationThrows_RollsBackAppliedEntries()
    {
        var (_, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        Assert.Throws<InvalidOperationException>(() => updater.AddOrUpdateEntries(ThrowingAfterFirst()));

        Assert.Equal(1, updater.EntryCount);
        Assert.False(updater.ContainsEntry(100));
        Assert.Same(published, provider.Current);
    }

    [Fact]
    public void RefreshEntry_WhenBuildThrows_RestoresThePreviousEntry()
    {
        var (engine, updater, _) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });

        Assert.ThrowsAny<Exception>(() => updater.RefreshEntry(1, Poisoned));
        Assert.ThrowsAny<Exception>(() => updater.RefreshEntry(2, Poisoned));

        Assert.Equal(1, updater.EntryCount);
        Assert.False(updater.ContainsEntry(2));
        Assert.Equal([1], Ids(engine, "alpha"));
    }

    [Fact]
    public void RebuildFrom_WhenBuildThrows_KeepsThePreviousEntries()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        Assert.ThrowsAny<Exception>(() => updater.RebuildFrom(new Dictionary<int, IndexedEntry> { [9] = Poisoned }));

        Assert.Equal(2, updater.EntryCount);
        Assert.True(updater.ContainsEntry(1));
        Assert.Same(published, provider.Current);
        updater.AddEntry(3, "gamma");
        Assert.Equal([1], Ids(engine, "alpha"));
    }

    [Fact]
    public async Task RebuildFromAsync_WhenBuildThrows_KeepsThePreviousEntries()
    {
        var (_, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        await Assert.ThrowsAnyAsync<Exception>(() => updater.RebuildFromAsync(PoisonedStream()));

        Assert.True(updater.ContainsEntry(1));
        Assert.False(updater.ContainsEntry(9));
        Assert.Equal(1, updater.EntryCount);
        Assert.Same(published, provider.Current);

        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> PoisonedStream()
        {
            await Task.Yield();
            yield return new(9, Poisoned);
        }
    }

    private static SearchTokenization PresetByName(string name) => name switch
    {
        "default" => SearchTokenization.Default,
        "filemask" => SearchTokenization.FileMask,
        _ => SearchTokenization.Create("_", " "),
    };

    [Theory]
    [InlineData("default")]
    [InlineData("filemask")]
    [InlineData("custom")]
    public void Clear_KeepsTheConfigurationOfTheUpdater(string preset)
        => AssertClearKeepsTheConfiguration(PresetByName(preset));

    // Compose needs Unicode normalization, so this preset is skipped in globalization-invariant mode.
    [NormalizationFact]
    public void Clear_KeepsTheConfigurationOfTheUpdater_WithUnicodeMatching()
        => AssertClearKeepsTheConfiguration(
            SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold));

    private static void AssertClearKeepsTheConfiguration(SearchTokenization tokenization)
    {
        var (engine, updater, provider) = Create(tokenization);
        var entries = new Dictionary<int, string>
        {
            [1] = "Report_Final.PDF",
            [2] = "report draft.txt",
            [3] = "STRASSE-notes.pdf",
            [4] = "Straße.pdf",
        };
        string[] queries = ["report", "final", "pdf", "strasse", "Straße", "report draft", "_final"];

        updater.RebuildFrom(entries);
        var before = queries.Select(q => Ids(engine, q)).ToArray();

        updater.Clear();

        Assert.Equal(tokenization.IndexSeparators, provider.Current.Tokenization.IndexSeparators);
        Assert.Equal(tokenization.QuerySeparators, provider.Current.Tokenization.QuerySeparators);
        Assert.Equal(tokenization.UnicodeMatching, provider.Current.Tokenization.UnicodeMatching);
        Assert.Equal(0, updater.EntryCount);
        Assert.All(queries, q => Assert.Empty(Ids(engine, q)));

        updater.RebuildFrom(entries);
        Assert.Equal(before, queries.Select(q => Ids(engine, q)).ToArray());
    }

    [Fact]
    public void RemovingTheLastEntry_KeepsTheConfigurationOfTheUpdater()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold);
        var (_, updater, provider) = Create(tokenization);
        updater.AddEntry(1, "only.txt");

        updater.RemoveEntry(1);

        Assert.Equal(UnicodeMatching.CaseFold, provider.Current.Tokenization.UnicodeMatching);
        Assert.Equal(tokenization.QuerySeparators, provider.Current.Tokenization.QuerySeparators);
    }

    [Fact]
    public void Clear_WithTheDefaultPreset_PublishesTheSharedEmptySnapshot()
    {
        var (_, updater, provider) = Create();
        updater.AddEntry(1, "a");

        updater.Clear();

        Assert.Same(SearchEngine.Snapshots.IndexSnapshot.Empty, provider.Current);
    }

    [Fact]
    public async Task RebuildFromAsync_DoesNotResumeOnTheCallersSynchronizationContext()
    {
        var (engine, updater, _) = Create();
        var context = new CountingSynchronizationContext();
        Task rebuild;

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            rebuild = updater.RebuildFromAsync(SlowStream());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        await rebuild;

        Assert.Equal(0, context.PostCount);
        Assert.Equal([1], Ids(engine, "alpha"));
    }

    // Completes asynchronously without capturing a context of its own, so only the updater could post to one.
    private static async IAsyncEnumerable<KeyValuePair<int, string>> SlowStream()
    {
        await Task.Delay(1).ConfigureAwait(false);
        yield return new(1, "alpha");
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            base.Post(d, state);
        }
    }
}
