using SearchEngine.Filters;
using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

// An Exact query for one plain term in snapshot order reads the live postings of its token straight from the snapshot,
// without a bitset (SearchEngineSharp.TryGetExactWordPostings, QueryExpressionEvaluator.TryGetSingleSemanticWord). This
// is the common type-ahead query, so it must answer exactly like the general path: with operators on or off, with a
// facet filter, and never for a glob term, whose postings under its literal text would be empty. Operators on and off
// without a filter are compared for every plain term of QueryExpressionTests.
public class SingleTermFastPathTests
{
    private static SearchEngineSharp CreateIndex(Func<long, FacetValues?>? facets = null)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", facets?.Invoke(1000)),
            [2] = new("report-draft.pdf", "report-draft.pdf", facets?.Invoke(5000)),
            [3] = new("notes.pdf", "notes.pdf", facets?.Invoke(1000)),
            [4] = new("foo-bar", "foo-bar", facets?.Invoke(1000)),
        });
        return engine;
    }

    // White-box: the classification that decides whether the shortcut is taken.
    [Test]
    [Arguments("report", true)]
    [Arguments("report*", true)]
    [Arguments("report final", false)]
    [Arguments("report AND final", false)]
    [Arguments("NOT report", false)]
    [Arguments("(report)", false)]
    public async Task A_query_is_one_term_only_without_operators_groups_or_a_second_term(string expression, bool expectedSingleWord)
    {
        bool actual = QueryExpressionEvaluator.TryGetSingleSemanticWord(
            expression.AsSpan(),
            enableOperators: true,
            SearchTokenization.Default,
            out _);

        await Assert.That(actual).IsEqualTo(expectedSingleWord);
    }

    // A glob term is one term, so only the metacharacter check keeps it off the shortcut. Under FileMask the same rule
    // is covered by TokenizationPresetTests (system*).
    [Test]
    [Arguments("report*", true, new[] { 1, 2 })]
    [Arguments("foo*", false, new[] { 4 })]
    public async Task A_glob_term_is_matched_as_a_glob_not_read_as_postings(string query, bool enableOperators, int[] expected)
    {
        var engine = CreateIndex();

        var results = engine.Find(query, WordMatchMethod.Exact, enableOperators);

        await Assert.That(results.Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Operators_on_or_off_give_the_same_single_term_result_with_a_filter()
    {
        var engine = CreateIndex(size => FacetValues.Create("size", size));
        var filter = FacetFilter.Range("size", 500, 2000);

        var off = engine.Find("report", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        var on = engine.Find("report", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(off).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(on).IsEquivalentTo(off, CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("report", WordMatchMethod.Exact, true, filter))
            .IsEqualTo(engine.CountMatches("report", WordMatchMethod.Exact, false, filter));
    }
}
