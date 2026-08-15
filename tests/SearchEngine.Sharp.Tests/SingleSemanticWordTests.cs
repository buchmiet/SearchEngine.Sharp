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

    [Theory]
    [InlineData("report", true)]
    [InlineData("report final", false)]
    [InlineData("report AND final", false)]
    [InlineData("NOT report", false)]
    [InlineData("(report)", false)]
    public void TryGetSingleSemanticWord_ClassifiesOperands(string expression, bool expectedSingleWord)
    {
        var separators = SearchTokenization.Default.QuerySeparatorValues;
        bool actual = QueryExpressionEvaluator.TryGetSingleSemanticWord(
            expression.AsSpan(),
            enableOperators: true,
            separators,
            out _);

        Assert.Equal(expectedSingleWord, actual);
    }

    [Fact]
    public void GlobMetacharacters_UseGlobMatcher_NotExactPostingFastPath()
    {
        var engine = CreateEngine();
        var separators = SearchTokenization.Default.QuerySeparatorValues;

        Assert.True(QueryExpressionEvaluator.TryGetSingleSemanticWord(
            "report*".AsSpan(),
            enableOperators: true,
            separators,
            out _));

        Assert.Equal(new[] { 1, 2 }, engine.Find("report*", WordMatchMethod.Exact, true).OrderBy(x => x));
    }

    [Fact]
    public void EnableOperatorsTrue_SingleTokenExactSameAsOff()
    {
        var engine = CreateEngine();

        Assert.Equal(
            engine.Find("report", WordMatchMethod.Exact, enableOperators: false),
            engine.Find("report", WordMatchMethod.Exact, enableOperators: true));

        Assert.Equal(
            engine.CountMatches("report", WordMatchMethod.Exact, enableOperators: false),
            engine.CountMatches("report", WordMatchMethod.Exact, enableOperators: true));
    }

    [Fact]
    public void EnableOperatorsTrue_SingleTokenExactFacetSameAsOff()
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

        Assert.Equal(off.OrderBy(x => x), on.OrderBy(x => x));
        Assert.Equal(
            engine.CountMatches("report", WordMatchMethod.Exact, false, filter),
            engine.CountMatches("report", WordMatchMethod.Exact, true, filter));
    }

    [Fact]
    public void EnableOperatorsTrue_BooleanExpression_StillEvaluates()
    {
        var engine = CreateEngine();

        Assert.Equal(new[] { 1, 2 }, engine.Find("report OR draft", WordMatchMethod.Exact, true).OrderBy(x => x));
        Assert.Equal(new[] { 1 }, engine.Find("report AND NOT draft", WordMatchMethod.Exact, true));
    }

    private static FacetValues? Facets(long size)
        => FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = size });
}
