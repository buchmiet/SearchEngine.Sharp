using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

// WordMatchMethod.Regex runs the whole expression as one .NET pattern against each unique indexed token, unanchored
// (src/Query/RegexMatcher.cs, docs/query-semantics.md, Regular expressions). A user can rely on it never throwing: an
// invalid pattern or one that times out matches nothing, and a construct the non-backtracking engine rejects runs on
// the backtracking engine instead. Operator terms are pattern text, and a pattern cannot match across the separators of
// the Default preset, only inside the whole name under FileMask. Facet filters and natural sort apply as for any other
// query. How a pattern gets the casing of the index is unit tested in RegexCaseFoldingTests and run against Unicode
// text in UnicodeMatchingTests (RegexPattern_*).
public class RegexMatchingTests
{
    // 1: report-final.pdf (tokens report, final, pdf), 2: reporting, 3: log, 4: abc, 5: aXc
    private static SearchEngineSharp CreateTokenIndex()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "reporting",
            [3] = "log",
            [4] = "abc",
            [5] = "aXc",
        });
        return engine;
    }

    [Test]
    [Arguments("^reporting$", new[] { 2 })]
    [Arguments("report", new[] { 1, 2 })]
    [Arguments("report.*", new[] { 1, 2 })]
    [Arguments("a[a-z]c", new[] { 4, 5 })]
    [Arguments("reporting|log", new[] { 2, 3 })]
    public async Task A_pattern_matches_anywhere_inside_a_token(string pattern, int[] expected)
    {
        var engine = CreateTokenIndex();

        var results = engine.Find(pattern, WordMatchMethod.Regex);

        await Assert.That(results.Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Under_Default_a_pattern_does_not_match_across_separators()
    {
        var engine = CreateTokenIndex();

        var results = engine.Find(@"report.*\.pdf", WordMatchMethod.Regex);

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task Under_FileMask_a_pattern_runs_against_the_whole_name()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "notes.txt",
            [3] = "my-report.pdf",
        });

        var whole = engine.Find(@"^report-final\.pdf$", WordMatchMethod.Regex);
        var extension = engine.Find(@"\.(pdf|txt)$", WordMatchMethod.Regex);
        var inside = engine.Find("report", WordMatchMethod.Regex);

        await Assert.That(whole).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(extension.Order()).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(inside.Order()).IsEquivalentTo(new[] { 1, 3 }, CollectionOrdering.Matching);
    }

    // As an expression, x OR y would find the names x and y; as a pattern it finds the name that contains "x or y".
    [Test]
    public async Task Operator_words_in_a_pattern_are_pattern_text()
    {
        var engine = CreateTokenIndex();
        var (fileMask, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "x OR y.txt", [2] = "x", [3] = "y" });

        var plain = engine.Find("log", WordMatchMethod.Regex, enableOperators: true);
        var withOperators = fileMask.Find("x OR y", WordMatchMethod.Regex, enableOperators: true);
        var withoutOperators = fileMask.Find("x OR y", WordMatchMethod.Regex, enableOperators: false);

        await Assert.That(plain).IsEquivalentTo(new[] { 3 }, CollectionOrdering.Matching);
        await Assert.That(withOperators).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(withoutOperators).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_invalid_pattern_matches_nothing()
    {
        var engine = CreateTokenIndex();

        var results = engine.Find("(unclosed", WordMatchMethod.Regex);
        int count = engine.CountMatches("(unclosed", WordMatchMethod.Regex);

        await Assert.That(results).IsEmpty();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    [Arguments("foo(?=bar)", "foobar", 1)]
    [Arguments(@"(a)\1", "baad", 1)]
    public async Task A_pattern_the_non_backtracking_engine_rejects_runs_on_the_backtracking_engine(
        string pattern,
        string searchText,
        int expectedDocId)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [expectedDocId] = searchText });

        var results = engine.Find(pattern, WordMatchMethod.Regex);
        int count = engine.CountMatches(pattern, WordMatchMethod.Regex);

        await Assert.That(results).IsEquivalentTo(new[] { expectedDocId }, CollectionOrdering.Matching);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task A_pattern_that_times_out_matches_nothing()
    {
        // (?=x) forces the backtracking fallback; (a+)+b against a long 'a' run
        // is the classic exponential-backtracking case and trips the 1 s timeout.
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = new string('a', 40) });

        var results = engine.Find(@"(a+)+b(?=x)", WordMatchMethod.Regex);
        int count = engine.CountMatches(@"(a+)+b(?=x)", WordMatchMethod.Regex);

        await Assert.That(results).IsEmpty();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task On_an_empty_index_a_pattern_matches_nothing()
    {
        var (engine, _, _) = TestIndex.Create();

        var results = engine.Find(".*", WordMatchMethod.Regex);
        int count = engine.CountMatches(".*", WordMatchMethod.Regex);

        await Assert.That(results).IsEmpty();
        await Assert.That(count).IsEqualTo(0);
    }

    // A blank expression is no query, for Regex as for the other methods, although an empty .NET pattern would match
    // every token: alone it finds nothing, and with a filter it selects what the filter selects.
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_blank_pattern_matches_nothing_and_with_a_filter_selects_what_the_filter_selects(string pattern)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("reporting", "reporting", FacetValues.Create("size", 100)),
            [2] = new("log", "log", FacetValues.Create("size", 500)),
        });
        var filter = FacetFilter.Range("size", 200, long.MaxValue);

        await Assert.That(engine.Find(pattern, WordMatchMethod.Regex)).IsEmpty();
        await Assert.That(engine.CountMatches(pattern, WordMatchMethod.Regex)).IsEqualTo(0);
        await Assert.That(NameMatcher.Create(pattern, WordMatchMethod.Regex).IsMatch("reporting")).IsFalse();
        await Assert.That(engine.Find(pattern, WordMatchMethod.Regex, false, SearchSortMode.SnapshotOrder, filter))
            .IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches(pattern, WordMatchMethod.Regex, false, filter)).IsEqualTo(1);
    }

    [Test]
    public async Task A_facet_filter_narrows_the_matches_of_a_pattern()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("reporting", "reporting", FacetValues.Create("size", 100)),
            [2] = new("log", "log", FacetValues.Create("size", 500)),
        });
        var filter = FacetFilter.Range("size", 200, long.MaxValue);

        var results = engine.Find("reporting|log", WordMatchMethod.Regex, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_matches_of_a_pattern_can_be_sorted_naturally()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [10] = "GA-100",
            [11] = "GA-10",
            [12] = "GA-2",
        });

        var results = engine.Find("ga-.*", WordMatchMethod.Regex, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo(new[] { 12, 11, 10 }, CollectionOrdering.Matching);
    }
}
