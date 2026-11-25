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

    [Test]
    public async Task Default_splits_a_query_at_a_tab()
    {
        var engine = Engine(SearchTokenization.Default, "a b", "a c");

        await Assert.That(engine.Find("a\tb", WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("a\tb", WordMatchMethod.Exact, enableOperators: true)).IsEqualTo(1);
        await Assert.That(NameMatcher.Create("a\tb", WordMatchMethod.Exact).IsMatch("a b")).IsTrue();
    }

    [Test]
    public async Task Default_uses_the_same_separators_for_queries_and_indexed_text()
        => await Assert.That(SearchTokenization.Default.QuerySeparators.Order().ToArray())
            .IsEquivalentTo(SearchTokenization.Default.IndexSeparators.Order().ToArray(), CollectionOrdering.Matching);

    [Test]
    public async Task FileMask_keeps_a_tab_inside_a_mask()
    {
        var engine = Engine(SearchTokenization.FileMask, "a\tb.txt", "a b.txt");

        await Assert.That(engine.Find("a\tb.txt", WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}
