using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Query;
using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

public class SingleSemanticWordTests
{
    private static SearchEngineSharp CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf"),
            [2] = new("report-draft.pdf", "report-draft.pdf"),
            [3] = new("notes.pdf", "notes.pdf"),
        });
        return new SearchEngineSharp(provider);
    }

    [Test]
    [Arguments("report", true)]
    [Arguments("report final", false)]
    [Arguments("report AND final", false)]
    [Arguments("NOT report", false)]
    [Arguments("(report)", false)]
    public async Task TryGetSingleSemanticWord_ClassifiesOperands(string expression, bool expectedSingleWord)
    {
        bool actual = QueryExpressionEvaluator.TryGetSingleSemanticWord(
            expression.AsSpan(),
            enableOperators: true,
            SearchTokenization.Default,
            out _);

        await Assert.That(actual).IsEqualTo(expectedSingleWord);
    }

    [Test]
    public async Task GlobMetacharacters_UseGlobMatcher_NotExactPostingFastPath()
    {
        var engine = CreateEngine();

        await Assert.That(QueryExpressionEvaluator.TryGetSingleSemanticWord(
            "report*".AsSpan(),
            enableOperators: true,
            SearchTokenization.Default,
            out _)).IsTrue();

        await Assert.That(engine.Find("report*", WordMatchMethod.Exact, true).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EnableOperatorsTrue_SingleTokenExactSameAsOff()
    {
        var engine = CreateEngine();

        await Assert.That(engine.Find("report", WordMatchMethod.Exact, enableOperators: true))
            .IsEquivalentTo(engine.Find("report", WordMatchMethod.Exact, enableOperators: false), CollectionOrdering.Matching);

        await Assert.That(engine.CountMatches("report", WordMatchMethod.Exact, enableOperators: true))
            .IsEqualTo(engine.CountMatches("report", WordMatchMethod.Exact, enableOperators: false));
    }

    [Test]
    public async Task EnableOperatorsTrue_SingleTokenExactFacetSameAsOff()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", Facets(1000)),
            [2] = new("report-draft.pdf", "report-draft.pdf", Facets(5000)),
        });
        var engine = new SearchEngineSharp(provider);
        var filter = FacetFilter.Range("size", 500, 2000);

        var off = engine.Find("report", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        var on = engine.Find("report", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(on.OrderBy(x => x)).IsEquivalentTo(off.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("report", WordMatchMethod.Exact, true, filter))
            .IsEqualTo(engine.CountMatches("report", WordMatchMethod.Exact, false, filter));
    }

    [Test]
    public async Task EnableOperatorsTrue_BooleanExpression_StillEvaluates()
    {
        var engine = CreateEngine();

        await Assert.That(engine.Find("report OR draft", WordMatchMethod.Exact, true).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("report AND NOT draft", WordMatchMethod.Exact, true)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    private static FacetValues? Facets(long size)
        => FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = size });
}
