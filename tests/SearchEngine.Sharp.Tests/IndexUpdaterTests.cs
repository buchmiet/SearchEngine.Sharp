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

    [Test]
    public async Task AddEntry_WhenBuildThrows_KeepsStoredEntriesAndPublishedSnapshot()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.AddEntry(3, Poisoned)).Throws<Exception>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(updater.ContainsEntry(3)).IsFalse();
        await Assert.That(provider.Current).IsSameReferenceAs(published);

        updater.AddEntry(4, "gamma");
        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(Ids(engine, "gamma")).IsEquivalentTo([4], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddEntry_WhenReplacingAndBuildThrows_RestoresThePreviousEntry()
    {
        var (engine, updater, _) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });

        await Assert.That(() => updater.AddEntry(1, Poisoned)).Throws<Exception>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        updater.AddEntry(3, "gamma");
        await Assert.That(Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "gamma")).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddOrUpdateEntries_WhenBuildThrows_RollsBackTheWholeBatch()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.AddOrUpdateEntries(new Dictionary<int, IndexedEntry>
        {
            [2] = new("replaced", "replaced"),
            [3] = new("added", "added"),
            [4] = Poisoned,
        })).Throws<Exception>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(provider.Current).IsSameReferenceAs(published);
        updater.AddEntry(5, "gamma");
        await Assert.That(Ids(engine, "beta")).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "replaced")).IsEmpty();
        await Assert.That(Ids(engine, "added")).IsEmpty();
    }

    [Test]
    public async Task AddOrUpdateEntries_WhenTheSourceEnumerationThrows_RollsBackAppliedEntries()
    {
        var (_, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        await Assert.That(() => updater.AddOrUpdateEntries(ThrowingAfterFirst())).ThrowsExactly<InvalidOperationException>();

        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(updater.ContainsEntry(100)).IsFalse();
        await Assert.That(provider.Current).IsSameReferenceAs(published);
    }

    [Test]
    public async Task RefreshEntry_WhenBuildThrows_RestoresThePreviousEntry()
    {
        var (engine, updater, _) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });

        await Assert.That(() => updater.RefreshEntry(1, Poisoned)).Throws<Exception>();
        await Assert.That(() => updater.RefreshEntry(2, Poisoned)).Throws<Exception>();

        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(updater.ContainsEntry(2)).IsFalse();
        await Assert.That(Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFrom_WhenBuildThrows_KeepsThePreviousEntries()
    {
        var (engine, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha", [2] = "beta" });
        var published = provider.Current;

        await Assert.That(() => updater.RebuildFrom(new Dictionary<int, IndexedEntry> { [9] = Poisoned })).Throws<Exception>();

        await Assert.That(updater.EntryCount).IsEqualTo(2);
        await Assert.That(updater.ContainsEntry(1)).IsTrue();
        await Assert.That(provider.Current).IsSameReferenceAs(published);
        updater.AddEntry(3, "gamma");
        await Assert.That(Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFromAsync_WhenBuildThrows_KeepsThePreviousEntries()
    {
        var (_, updater, provider) = Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "alpha" });
        var published = provider.Current;

        await Assert.That(() => updater.RebuildFromAsync(PoisonedStream())).Throws<Exception>();

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

    public static IEnumerable<string> Presets() => ["default", "filemask", "custom", "unicode"];

    private static SearchTokenization PresetByName(string name) => name switch
    {
        "default" => SearchTokenization.Default,
        "filemask" => SearchTokenization.FileMask,
        "custom" => SearchTokenization.Create("_", " "),
        _ => SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
    };

    [Test]
    [MethodDataSource(nameof(Presets))]
    public async Task Clear_KeepsTheConfigurationOfTheUpdater(string preset)
    {
        // The unicode preset composes, which needs Unicode normalization.
        if (preset == "unicode")
            Skip.Unless(TextFolder.IsNormalizationAvailable, SkipUnlessNormalizationAttribute.SkipReason);

        var tokenization = PresetByName(preset);
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

        await Assert.That(provider.Current.Tokenization.IndexSeparators).IsEqualTo(tokenization.IndexSeparators);
        await Assert.That(provider.Current.Tokenization.QuerySeparators).IsEqualTo(tokenization.QuerySeparators);
        await Assert.That(provider.Current.Tokenization.UnicodeMatching).IsEqualTo(tokenization.UnicodeMatching);
        await Assert.That(updater.EntryCount).IsEqualTo(0);
        foreach (string q in queries)
            await Assert.That(Ids(engine, q)).IsEmpty();

        updater.RebuildFrom(entries);
        await Assert.That(queries.Select(q => Ids(engine, q)).ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemovingTheLastEntry_KeepsTheConfigurationOfTheUpdater()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold);
        var (_, updater, provider) = Create(tokenization);
        updater.AddEntry(1, "only.txt");

        updater.RemoveEntry(1);

        await Assert.That(provider.Current.Tokenization.UnicodeMatching).IsEqualTo(UnicodeMatching.CaseFold);
        await Assert.That(provider.Current.Tokenization.QuerySeparators).IsEqualTo(tokenization.QuerySeparators);
    }

    [Test]
    public async Task Clear_WithTheDefaultPreset_PublishesTheSharedEmptySnapshot()
    {
        var (_, updater, provider) = Create();
        updater.AddEntry(1, "a");

        updater.Clear();

        await Assert.That(provider.Current).IsSameReferenceAs(SearchEngine.Snapshots.IndexSnapshot.Empty);
    }
}
