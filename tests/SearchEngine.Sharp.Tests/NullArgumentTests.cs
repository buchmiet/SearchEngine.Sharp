using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;
using SearchEngine.Filters;
using SearchEngine.Ingestion;
using SearchEngine.Snapshots;
using SearchEngine.Sorting;
using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

// Every public entry point rejects a null reference argument with ArgumentNullException naming the parameter, at
// the call. A null element inside a collection argument is ArgumentException naming the collection.
public class NullArgumentTests
{
    private static readonly IndexSnapshotProvider Provider = new();

    private static void AssertNull(string paramName, Action call)
        => Assert.Equal(paramName, Assert.Throws<ArgumentNullException>(call).ParamName);

    private static void AssertInvalidElement(string paramName, Action call)
    {
        var exception = Assert.Throws<ArgumentException>(call);
        Assert.IsNotType<ArgumentNullException>(exception);
        Assert.Equal(paramName, exception.ParamName);
    }

    // ── Queries ──

    [Fact]
    public void SearchEngineSharp_rejects_a_null_provider()
        => AssertNull("snapshotProvider", () => new SearchEngineSharp(null!));

    [Fact]
    public void Find_and_CountMatches_reject_a_null_expression()
    {
        var engine = new SearchEngineSharp(Provider);

        AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact));
        AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending));
        AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("n", 0, 1)));
        AssertNull("expression", () => engine.CountMatches(null!, WordMatchMethod.Within));
        AssertNull("expression", () => engine.CountMatches(null!, WordMatchMethod.Within, false, FacetFilter.Range("n", 0, 1)));
    }

    [Fact]
    public void NameMatcher_rejects_a_null_expression()
        => AssertNull("expression", () => NameMatcher.Create(null!, WordMatchMethod.Exact));

    // ── Tokenization and entries ──

    [Fact]
    public void SearchTokenization_Create_rejects_null_separators()
    {
        AssertNull("indexSeparators", () => SearchTokenization.Create(null!, " "));
        AssertNull("querySeparators", () => SearchTokenization.Create(" ", null!));
    }

    [Fact]
    public void IndexedEntry_rejects_null_texts()
    {
        var entry = new IndexedEntry("search", "sort");

        AssertNull("SearchText", () => _ = new IndexedEntry(null!, "sort"));
        AssertNull("SortText", () => _ = new IndexedEntry("search", null!));
        AssertNull("SearchText", () => _ = entry with { SearchText = null! });
        AssertNull("SortText", () => _ = entry with { SortText = null! });
    }

    // ── Facets ──

    [Fact]
    public void FacetFilter_factories_reject_null_arguments()
    {
        AssertNull("facet", () => FacetFilter.Range(null!, 0, 1));
        AssertNull("facet", () => FacetFilter.Mask(null!, 1));
        AssertNull("filters", () => FacetFilter.Combine(null!));
        AssertInvalidElement("filters", () => FacetFilter.Combine(FacetFilter.Range("n", 0, 1), null!));
    }

    [Fact]
    public void FacetValues_Create_rejects_a_null_name()
        => AssertNull("facet", () => FacetValues.Create(null!, 1));

    // ── Updater ──

    [Fact]
    public void IndexUpdater_rejects_a_null_provider()
        => AssertNull("provider", () => new IndexUpdater(null!));

    [Fact]
    public void IndexUpdater_rejects_null_arguments()
    {
        var updater = new IndexUpdater(new IndexSnapshotProvider());

        AssertNull("entries", () => updater.RebuildFrom((IDictionary<int, string>)null!));
        AssertNull("entries", () => updater.RebuildFrom((IDictionary<int, IndexedEntry>)null!));
        AssertNull("entries", () => updater.RebuildFromAsync((IAsyncEnumerable<KeyValuePair<int, string>>)null!));
        AssertNull("entries", () => updater.RebuildFromAsync((IAsyncEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
        AssertNull("text", () => updater.AddEntry(1, (string)null!));
        AssertNull("entry", () => updater.AddEntry(1, (IndexedEntry)null!));
        AssertNull("text", () => updater.RefreshEntry(1, (string)null!));
        AssertNull("entry", () => updater.RefreshEntry(1, (IndexedEntry)null!));
        AssertNull("entries", () => updater.AddOrUpdateEntries((IEnumerable<KeyValuePair<int, string>>)null!));
        AssertNull("entries", () => updater.AddOrUpdateEntries((IEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
        AssertNull("ids", () => updater.RemoveEntries(null!));
        Assert.Equal(0, updater.EntryCount);
    }

    [Fact]
    public void IndexUpdater_rejects_null_entries_inside_its_input_and_keeps_the_index()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.AddEntry(1, "kept");
        var published = provider.Current;

        AssertInvalidElement("entries", () => updater.RebuildFrom(new Dictionary<int, string> { [2] = null! }));
        AssertInvalidElement("entries", () => updater.RebuildFrom(new Dictionary<int, IndexedEntry> { [2] = null! }));
        AssertInvalidElement("entries", () => updater.AddOrUpdateEntries([new KeyValuePair<int, string>(3, "new"), new(4, null!)]));
        AssertInvalidElement("entries", () => updater.AddOrUpdateEntries([new KeyValuePair<int, IndexedEntry>(3, null!)]));

        Assert.Same(published, provider.Current);
        Assert.Equal(1, updater.EntryCount);
        Assert.False(updater.ContainsEntry(3));
    }

    [Fact]
    public async Task RebuildFromAsync_faults_for_a_null_entry_in_the_stream()
    {
        static async IAsyncEnumerable<KeyValuePair<int, string>> Entries()
        {
            await Task.Yield();
            yield return new(1, null!);
        }

        var updater = new IndexUpdater(new IndexSnapshotProvider());

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => updater.RebuildFromAsync(Entries()));

        Assert.Equal("entries", exception.ParamName);
    }

    // ── Builder and sort key ──

    [Fact]
    public void IndexSnapshotBuilder_rejects_null_arguments()
    {
        KeyValuePair<int, string>[] texts = [new(1, "a")];
        KeyValuePair<int, IndexedEntry>[] entries = [new(1, new IndexedEntry("a", "a"))];

        AssertNull("entries", () => IndexSnapshotBuilder.Build((IEnumerable<KeyValuePair<int, string>>)null!));
        AssertNull("entries", () => IndexSnapshotBuilder.Build((IEnumerable<KeyValuePair<int, IndexedEntry>>)null!, progress: null));
        AssertNull("tokenization", () => IndexSnapshotBuilder.Build(texts, (SearchTokenization)null!));
        AssertNull("tokenization", () => IndexSnapshotBuilder.Build(entries, null!, progress: null));
        AssertInvalidElement("entries", () => IndexSnapshotBuilder.Build([new KeyValuePair<int, string>(1, null!)]));
        AssertInvalidElement("entries", () => IndexSnapshotBuilder.Build([new KeyValuePair<int, IndexedEntry>(1, null!)]));
    }

    [Fact]
    public void NaturalSortKeyBuilder_rejects_a_null_text()
        => AssertNull("sortText", () => NaturalSortKeyBuilder.Build(null!));

    // ── Ingestion ──

    [Fact]
    public void ProgressiveIndexIngestion_rejects_null_arguments_at_the_call()
    {
        AssertNull("updater", () => _ = new ProgressiveIndexIngestion(null!));

        var ingestion = new ProgressiveIndexIngestion(new IndexUpdater(new IndexSnapshotProvider()));
        AssertNull("entries", () => ingestion.IngestAsync((IAsyncEnumerable<KeyValuePair<int, string>>)null!));
        AssertNull("entries", () => ingestion.IngestAsync((IAsyncEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
    }

    [Fact]
    public async Task IngestAsync_faults_for_a_null_entry_in_the_scan()
    {
        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Entries()
        {
            await Task.Yield();
            yield return new(1, null!);
        }

        var ingestion = new ProgressiveIndexIngestion(new IndexUpdater(new IndexSnapshotProvider()));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => ingestion.IngestAsync(Entries()));

        Assert.Equal("entries", exception.ParamName);
    }

    // ── Tokenizer helpers ──

    [Fact]
    public void NameTokenizer_rejects_a_null_join_text()
        => AssertNull("joinCharacter", () => NameTokenizer.TokenizeName("a b", null!));

    [Fact]
    public void TextNormalizer_rejects_null_arguments()
    {
        var separators = SearchValues.Create(" ");
        var set = new HashSet<string>(StringComparer.Ordinal);
        int state = 0;
        TextNormalizer.UniqueWordAction<int> action = static (ref int count, string _) => count++;

        AssertNull("separators", () => TextNormalizer.IsSeparator(' ', null!));
        AssertNull("text", () => TextNormalizer.GetWordsFromString(null!));
        AssertNull("text", () => TextNormalizer.GetWordsFromString(null!, separators));
        AssertNull("separators", () => TextNormalizer.GetWordsFromString("a", null!));
        AssertNull("text", () => TextNormalizer.ForEachUniqueWord(null!, set, ref state, action));
        AssertNull("uniqueWords", () => TextNormalizer.ForEachUniqueWord("a", null!, ref state, action));
        AssertNull("action", () => TextNormalizer.ForEachUniqueWord("a", set, ref state, null!));
        AssertNull("separators", () => TextNormalizer.ForEachUniqueWord("a", set, null!, ref state, action));
    }

    // ── Dependency injection ──

    [Fact]
    public void Registration_methods_reject_null_arguments()
    {
        var services = new ServiceCollection();
        var entries = new Dictionary<int, string> { [1] = "a" };
        Func<IServiceProvider, IDictionary<int, string>> factory = _ => entries;

        AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!));
        AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngineTransient(null!));
        AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!, entries));
        AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!, factory));
        AssertNull("services", () => ServiceCollectionExtensions.AddKeyedSearchEngine(null!, "k"));
        AssertNull("services", () => ServiceCollectionExtensions.AddKeyedSearchEngine(null!, "k", entries));
        AssertNull("tokenization", () => services.AddSearchEngine((SearchTokenization)null!));
        AssertNull("tokenization", () => services.AddSearchEngineTransient(null!));
        AssertNull("tokenization", () => services.AddSearchEngine(entries, null!));
        AssertNull("tokenization", () => services.AddSearchEngine(factory, null!));
        AssertNull("tokenization", () => services.AddKeyedSearchEngine("k", (SearchTokenization)null!));
        AssertNull("tokenization", () => services.AddKeyedSearchEngine("k", entries, null!));
        AssertNull("initialEntries", () => services.AddSearchEngine((IDictionary<int, string>)null!));
        AssertNull("initialEntries", () => services.AddKeyedSearchEngine("k", (IDictionary<int, string>)null!));
        AssertNull("entriesFactory", () => services.AddSearchEngine((Func<IServiceProvider, IDictionary<int, string>>)null!));
        AssertNull("key", () => services.AddKeyedSearchEngine(null!));
        AssertNull("key", () => services.AddKeyedSearchEngine(null!, entries));
        AssertInvalidElement("initialEntries", () => services.AddSearchEngine(new Dictionary<int, string> { [1] = null! }));
        Assert.Empty(services);
    }

    [Fact]
    public void A_null_dictionary_from_the_entries_factory_fails_when_the_index_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddSearchEngine(_ => null!);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IIndexSnapshotProvider>());
    }
}
