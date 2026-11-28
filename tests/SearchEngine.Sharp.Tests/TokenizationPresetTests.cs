using SearchEngine.Filters;
using SearchEngine.Ingestion;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// A SearchTokenization preset decides where indexed text and queries split into tokens (src/SearchTokenization.cs).
// Default splits both at the same punctuation, a tab included, so a pasted query finds what it shows. FileMask keeps
// the whole name as one token and splits queries only at space, CR and LF, so *.pdf matches names that end in .pdf and
// a tab stays part of a mask. Every snapshot keeps the preset it was built with, whichever way it was published, and
// every query on it uses that preset.
public class TokenizationPresetTests
{
    // 1: report-final.pdf, 2: System, 3: my-system-backup.zip, 4: notes.txt
    private static SearchEngineSharp CreateFileMaskIndex()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
            [3] = "my-system-backup.zip",
            [4] = "notes.txt",
        });
        return engine;
    }

    // A bare term must equal the whole name: system finds System, not my-system-backup.zip.
    [Test]
    [Arguments("system", new[] { 2 })]
    [Arguments("system*", new[] { 2 })]
    [Arguments("*.pdf", new[] { 1 })]
    [Arguments("*system*", new[] { 2, 3 })]
    [Arguments("*.pdf OR *.txt", new[] { 1, 4 })]
    [Arguments("* AND NOT *.zip", new[] { 1, 2, 4 })]
    [Arguments("*", new[] { 1, 2, 3, 4 })]
    public async Task FileMask_matches_whole_names_and_globs(string query, int[] expectedIds)
    {
        var engine = CreateFileMaskIndex();
        bool[] operatorModes = query.Contains(" OR ") || query.Contains(" NOT ") ? [true] : [true, false];

        foreach (bool operators in operatorModes)
        {
            await Assert.That(engine.Find(query, WordMatchMethod.Exact, operators).Order()).IsEquivalentTo(expectedIds, CollectionOrdering.Matching);
            await Assert.That(engine.CountMatches(query, WordMatchMethod.Exact, operators)).IsEqualTo(expectedIds.Length);
        }
    }

    [Test]
    public async Task Within_under_FileMask_matches_inside_the_whole_name()
    {
        var engine = CreateFileMaskIndex();

        var results = engine.Find("port", WordMatchMethod.Within, enableOperators: false);

        await Assert.That(results).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_facet_filter_works_under_FileMask()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", FacetValues.Create("size", 100)),
            [2] = new("System", "System", FacetValues.Create("size", 500)),
        });
        var filter = FacetFilter.Range("size", 200, long.MaxValue);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Natural_sort_works_under_FileMask()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [10] = "GA-100",
            [11] = "GA-10",
            [12] = "GA-2",
        });

        var results = engine.Find("ga*", WordMatchMethod.Exact, enableOperators: false, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo(new[] { 12, 11, 10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_keeps_a_tab_inside_a_mask()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "a\tb.txt", [2] = "a b.txt" });

        var results = engine.Find("a\tb.txt", WordMatchMethod.Exact);

        await Assert.That(results).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_default_preset_splits_names_at_punctuation()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.Default);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
        });

        var within = engine.Find("report", WordMatchMethod.Within);
        var exactOr = engine.Find("report OR system", WordMatchMethod.Exact, enableOperators: true);

        await Assert.That(provider.Current.Tokenization).IsEqualTo(SearchTokenization.Default);
        await Assert.That(within).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(exactOr).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Default_splits_a_query_at_a_tab()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.Default);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "a b", [2] = "a c" });

        var results = engine.Find("a\tb", WordMatchMethod.Exact);

        await Assert.That(results).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("a\tb", WordMatchMethod.Exact, enableOperators: true)).IsEqualTo(1);
        await Assert.That(NameMatcher.Create("a\tb", WordMatchMethod.Exact).IsMatch("a b")).IsTrue();
    }

    [Test]
    public async Task Default_uses_the_same_separators_for_queries_and_indexed_text()
    {
        var querySeparators = SearchTokenization.Default.QuerySeparators.Order().ToArray();
        var indexSeparators = SearchTokenization.Default.IndexSeparators.Order().ToArray();

        await Assert.That(querySeparators).IsEquivalentTo(indexSeparators, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_custom_preset_splits_at_its_own_separators()
    {
        var custom = SearchTokenization.Create("-", " ");

        var snapshot = IndexSnapshotBuilder.Build(new Dictionary<int, string> { [1] = "a-b-c" }, custom);

        await Assert.That(custom.IndexSeparators).IsEqualTo("-");
        await Assert.That(custom.QuerySeparators).IsEqualTo(" ");
        await Assert.That(snapshot.Tokenization).IsEqualTo(custom);
        await Assert.That(snapshot.UniqueWordCount).IsEqualTo(3);
    }

    [Test]
    public async Task Progressive_ingestion_publishes_snapshots_with_the_updater_preset()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        var ingestion = new ProgressiveIndexIngestion(updater);

        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            yield return new(1, new IndexedEntry("report-final.pdf", "report-final.pdf"));
            yield return new(2, new IndexedEntry("System", "System"));
            yield return new(3, new IndexedEntry("my-system-backup.zip", "my-system-backup.zip"));
            await Task.Yield();
            yield return new(4, new IndexedEntry("notes.txt", "notes.txt"));
        }

        var result = await ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions { Policy = new IngestPublishPolicy.FixedBatch { BatchSize = 2 } });

        await Assert.That(result.CompletedSuccessfully).IsTrue();
        await Assert.That(provider.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("system", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    // White-box (allocation): a whole-name token that is already lowercase is the entry's own string, not a copy.
    [Test]
    public async Task A_lowercase_whole_name_token_reuses_the_entry_string()
    {
        string lower = string.Concat("x", "report-final.pdf"); // runtime instance, not an interned literal
        string mixed = "Report-Final.PDF";

        var snapshot = IndexSnapshotBuilder.Build(
            new Dictionary<int, string> { [1] = lower, [2] = mixed },
            SearchTokenization.FileMask);

        await Assert.That(snapshot.Segments[0].ExactPostings.Keys).Contains(key => ReferenceEquals(key, lower));
        await Assert.That(snapshot.Segments[0].ExactPostings.Keys).Contains("report-final.pdf"); // lowercased copy of the mixed-case name
        await Assert.That(snapshot.Segments[0].ExactPostings.Keys).DoesNotContain(key => ReferenceEquals(key, mixed));
    }
}
