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

    // The helpers take an Action, so only a throw at the call counts: a returned task that faults later does not.
    private static async Task AssertNull(string paramName, Action call)
    {
        var exception = await Assert.That(call).ThrowsExactly<ArgumentNullException>();
        await Assert.That(exception!.ParamName).IsEqualTo(paramName);
    }

    private static async Task AssertInvalidElement(string paramName, Action call)
    {
        var exception = await Assert.That(call).ThrowsExactly<ArgumentException>();
        await Assert.That(exception).IsNotTypeOf<ArgumentNullException>();
        await Assert.That(exception!.ParamName).IsEqualTo(paramName);
    }

    // ── Queries ──

    [Test]
    public async Task SearchEngineSharp_rejects_a_null_provider()
        => await AssertNull("snapshotProvider", () => new SearchEngineSharp(null!));

    [Test]
    public async Task Find_and_CountMatches_reject_a_null_expression()
    {
        var engine = new SearchEngineSharp(Provider);

        await AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact));
        await AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending));
        await AssertNull("expression", () => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("n", 0, 1)));
        await AssertNull("expression", () => engine.CountMatches(null!, WordMatchMethod.Within));
        await AssertNull("expression", () => engine.CountMatches(null!, WordMatchMethod.Within, false, FacetFilter.Range("n", 0, 1)));
    }

    [Test]
    public async Task NameMatcher_rejects_a_null_expression()
        => await AssertNull("expression", () => NameMatcher.Create(null!, WordMatchMethod.Exact));

    // ── Tokenization and entries ──

    [Test]
    public async Task SearchTokenization_Create_rejects_null_separators()
    {
        await AssertNull("indexSeparators", () => SearchTokenization.Create(null!, " "));
        await AssertNull("querySeparators", () => SearchTokenization.Create(" ", null!));
    }

    [Test]
    public async Task IndexedEntry_rejects_null_texts()
    {
        var entry = new IndexedEntry("search", "sort");

        await AssertNull("SearchText", () => _ = new IndexedEntry(null!, "sort"));
        await AssertNull("SortText", () => _ = new IndexedEntry("search", null!));
        await AssertNull("SearchText", () => _ = entry with { SearchText = null! });
        await AssertNull("SortText", () => _ = entry with { SortText = null! });
    }

    // ── Facets ──

    [Test]
    public async Task FacetFilter_factories_reject_null_arguments()
    {
        await AssertNull("facet", () => FacetFilter.Range(null!, 0, 1));
        await AssertNull("facet", () => FacetFilter.Mask(null!, 1));
        await AssertNull("filters", () => FacetFilter.Combine(null!));
        await AssertInvalidElement("filters", () => FacetFilter.Combine(FacetFilter.Range("n", 0, 1), null!));
    }

    [Test]
    public async Task FacetValues_Create_rejects_a_null_name()
        => await AssertNull("facet", () => FacetValues.Create(null!, 1));

    // ── Updater ──

    [Test]
    public async Task IndexUpdater_rejects_a_null_provider()
        => await AssertNull("provider", () => new IndexUpdater(null!));

    [Test]
    public async Task IndexUpdater_rejects_null_arguments()
    {
        var updater = new IndexUpdater(new IndexSnapshotProvider());

        await AssertNull("entries", () => updater.RebuildFrom((IDictionary<int, string>)null!));
        await AssertNull("entries", () => updater.RebuildFrom((IDictionary<int, IndexedEntry>)null!));
        await AssertNull("entries", () => updater.RebuildFromAsync((IAsyncEnumerable<KeyValuePair<int, string>>)null!));
        await AssertNull("entries", () => updater.RebuildFromAsync((IAsyncEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
        await AssertNull("text", () => updater.AddEntry(1, (string)null!));
        await AssertNull("entry", () => updater.AddEntry(1, (IndexedEntry)null!));
        await AssertNull("text", () => updater.RefreshEntry(1, (string)null!));
        await AssertNull("entry", () => updater.RefreshEntry(1, (IndexedEntry)null!));
        await AssertNull("entries", () => updater.AddOrUpdateEntries((IEnumerable<KeyValuePair<int, string>>)null!));
        await AssertNull("entries", () => updater.AddOrUpdateEntries((IEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
        await AssertNull("ids", () => updater.RemoveEntries(null!));
        await Assert.That(updater.EntryCount).IsEqualTo(0);
    }

    [Test]
    public async Task IndexUpdater_rejects_null_entries_inside_its_input_and_keeps_the_index()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.AddEntry(1, "kept");
        var published = provider.Current;

        await AssertInvalidElement("entries", () => updater.RebuildFrom(new Dictionary<int, string> { [2] = null! }));
        await AssertInvalidElement("entries", () => updater.RebuildFrom(new Dictionary<int, IndexedEntry> { [2] = null! }));
        await AssertInvalidElement("entries", () => updater.AddOrUpdateEntries([new KeyValuePair<int, string>(3, "new"), new(4, null!)]));
        await AssertInvalidElement("entries", () => updater.AddOrUpdateEntries([new KeyValuePair<int, IndexedEntry>(3, null!)]));

        await Assert.That(provider.Current).IsSameReferenceAs(published);
        await Assert.That(updater.EntryCount).IsEqualTo(1);
        await Assert.That(updater.ContainsEntry(3)).IsFalse();
    }

    [Test]
    public async Task RebuildFromAsync_faults_for_a_null_entry_in_the_stream()
    {
        static async IAsyncEnumerable<KeyValuePair<int, string>> Entries()
        {
            await Task.Yield();
            yield return new(1, null!);
        }

        var updater = new IndexUpdater(new IndexSnapshotProvider());

        var exception = await Assert.That(() => updater.RebuildFromAsync(Entries())).ThrowsExactly<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("entries");
    }

    // ── Builder and sort key ──

    [Test]
    public async Task IndexSnapshotBuilder_rejects_null_arguments()
    {
        KeyValuePair<int, string>[] texts = [new(1, "a")];
        KeyValuePair<int, IndexedEntry>[] entries = [new(1, new IndexedEntry("a", "a"))];

        await AssertNull("entries", () => IndexSnapshotBuilder.Build((IEnumerable<KeyValuePair<int, string>>)null!));
        await AssertNull("entries", () => IndexSnapshotBuilder.Build((IEnumerable<KeyValuePair<int, IndexedEntry>>)null!, progress: null));
        await AssertNull("tokenization", () => IndexSnapshotBuilder.Build(texts, (SearchTokenization)null!));
        await AssertNull("tokenization", () => IndexSnapshotBuilder.Build(entries, null!, progress: null));
        await AssertInvalidElement("entries", () => IndexSnapshotBuilder.Build([new KeyValuePair<int, string>(1, null!)]));
        await AssertInvalidElement("entries", () => IndexSnapshotBuilder.Build([new KeyValuePair<int, IndexedEntry>(1, null!)]));
    }

    [Test]
    public async Task NaturalSortKeyBuilder_rejects_a_null_text()
        => await AssertNull("sortText", () => NaturalSortKeyBuilder.Build(null!));

    // ── Ingestion ──

    [Test]
    public async Task ProgressiveIndexIngestion_rejects_null_arguments_at_the_call()
    {
        await AssertNull("updater", () => _ = new ProgressiveIndexIngestion(null!));

        var ingestion = new ProgressiveIndexIngestion(new IndexUpdater(new IndexSnapshotProvider()));
        await AssertNull("entries", () => ingestion.IngestAsync((IAsyncEnumerable<KeyValuePair<int, string>>)null!));
        await AssertNull("entries", () => ingestion.IngestAsync((IAsyncEnumerable<KeyValuePair<int, IndexedEntry>>)null!));
    }

    [Test]
    public async Task IngestAsync_faults_for_a_null_entry_in_the_scan()
    {
        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Entries()
        {
            await Task.Yield();
            yield return new(1, null!);
        }

        var ingestion = new ProgressiveIndexIngestion(new IndexUpdater(new IndexSnapshotProvider()));

        var exception = await Assert.That(() => (Task)ingestion.IngestAsync(Entries())).ThrowsExactly<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("entries");
    }

    // ── Tokenizer helpers ──

    [Test]
    public async Task NameTokenizer_rejects_a_null_join_text()
        => await AssertNull("joinCharacter", () => NameTokenizer.TokenizeName("a b", null!));

    [Test]
    public async Task TextNormalizer_rejects_null_arguments()
    {
        var separators = SearchValues.Create(" ");
        var set = new HashSet<string>(StringComparer.Ordinal);
        int state = 0;
        TextNormalizer.UniqueWordAction<int> action = static (ref int count, string _) => count++;

        await AssertNull("separators", () => TextNormalizer.IsSeparator(' ', null!));
        await AssertNull("text", () => TextNormalizer.GetWordsFromString(null!));
        await AssertNull("text", () => TextNormalizer.GetWordsFromString(null!, separators));
        await AssertNull("separators", () => TextNormalizer.GetWordsFromString("a", null!));
        await AssertNull("text", () => TextNormalizer.ForEachUniqueWord(null!, set, ref state, action));
        await AssertNull("uniqueWords", () => TextNormalizer.ForEachUniqueWord("a", null!, ref state, action));
        await AssertNull("action", () => TextNormalizer.ForEachUniqueWord("a", set, ref state, null!));
        await AssertNull("separators", () => TextNormalizer.ForEachUniqueWord("a", set, null!, ref state, action));
    }

    // ── Dependency injection ──

    [Test]
    public async Task Registration_methods_reject_null_arguments()
    {
        var services = new ServiceCollection();
        var entries = new Dictionary<int, string> { [1] = "a" };
        Func<IServiceProvider, IDictionary<int, string>> factory = _ => entries;

        await AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!));
        await AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngineTransient(null!));
        await AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!, entries));
        await AssertNull("services", () => ServiceCollectionExtensions.AddSearchEngine(null!, factory));
        await AssertNull("services", () => ServiceCollectionExtensions.AddKeyedSearchEngine(null!, "k"));
        await AssertNull("services", () => ServiceCollectionExtensions.AddKeyedSearchEngine(null!, "k", entries));
        await AssertNull("tokenization", () => services.AddSearchEngine((SearchTokenization)null!));
        await AssertNull("tokenization", () => services.AddSearchEngineTransient(null!));
        await AssertNull("tokenization", () => services.AddSearchEngine(entries, null!));
        await AssertNull("tokenization", () => services.AddSearchEngine(factory, null!));
        await AssertNull("tokenization", () => services.AddKeyedSearchEngine("k", (SearchTokenization)null!));
        await AssertNull("tokenization", () => services.AddKeyedSearchEngine("k", entries, null!));
        await AssertNull("initialEntries", () => services.AddSearchEngine((IDictionary<int, string>)null!));
        await AssertNull("initialEntries", () => services.AddKeyedSearchEngine("k", (IDictionary<int, string>)null!));
        await AssertNull("entriesFactory", () => services.AddSearchEngine((Func<IServiceProvider, IDictionary<int, string>>)null!));
        await AssertNull("key", () => services.AddKeyedSearchEngine(null!));
        await AssertNull("key", () => services.AddKeyedSearchEngine(null!, entries));
        await AssertInvalidElement("initialEntries", () => services.AddSearchEngine(new Dictionary<int, string> { [1] = null! }));
        await Assert.That(services).IsEmpty();
    }

    [Test]
    public async Task A_null_dictionary_from_the_entries_factory_fails_when_the_index_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddSearchEngine(_ => null!);
        using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IIndexSnapshotProvider>()).ThrowsExactly<InvalidOperationException>();
    }
}
