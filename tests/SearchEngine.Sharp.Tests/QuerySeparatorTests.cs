namespace SearchEngine.Sharp.Tests;

public class QuerySeparatorTests
{
    private static SearchEngineSharp Engine(SearchTokenization tokenization, params string[] texts)
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, tokenization);
        updater.RebuildFrom(texts.Select((text, i) => (i + 1, text)).ToDictionary(p => p.Item1, p => p.text));
        return new SearchEngineSharp(provider);
    }

    [Fact]
    public void Default_splits_a_query_at_a_tab()
    {
        var engine = Engine(SearchTokenization.Default, "a b", "a c");

        Assert.Equal([1], engine.Find("a\tb", WordMatchMethod.Exact));
        Assert.Equal(1, engine.CountMatches("a\tb", WordMatchMethod.Exact, enableOperators: true));
        Assert.True(NameMatcher.Create("a\tb", WordMatchMethod.Exact).IsMatch("a b"));
    }

    [Fact]
    public void Default_uses_the_same_separators_for_queries_and_indexed_text()
        => Assert.Equal(
            SearchTokenization.Default.IndexSeparators.Order().ToArray(),
            SearchTokenization.Default.QuerySeparators.Order().ToArray());

    [Fact]
    public void FileMask_keeps_a_tab_inside_a_mask()
    {
        var engine = Engine(SearchTokenization.FileMask, "a\tb.txt", "a b.txt");

        Assert.Equal([1], engine.Find("a\tb.txt", WordMatchMethod.Exact));
    }
}
