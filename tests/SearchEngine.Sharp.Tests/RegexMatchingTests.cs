using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class RegexMatchingTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater);
    }

    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateTokenIndex()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "reporting",
            [3] = "log",
            [4] = "abc",
            [5] = "aXc",
        });
        return (engine, updater);
    }

    [Fact]
    public void Regex_UnanchoredMatch_MatchesSubstringWithinToken()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Equal(new[] { 2 }, engine.Find("^reporting$", WordMatchMethod.Regex));
        var reportMatches = engine.Find("report", WordMatchMethod.Regex).OrderBy(x => x);
        Assert.Equal(new[] { 1, 2 }, reportMatches);
    }

    [Fact]
    public void Regex_PrefixPattern_MatchesTokensContainingPrefix()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Equal(new[] { 1, 2 }, engine.Find("report.*", WordMatchMethod.Regex).OrderBy(x => x));
    }

    [Fact]
    public void Regex_IgnoreCase_MatchesNormalizedTokens()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "Report-Final.PDF" });

        Assert.Equal(new[] { 1 }, engine.Find("REPORT", WordMatchMethod.Regex));
        Assert.Equal(new[] { 1 }, engine.Find("[Rr][Ee][Pp][Oo][Rr][Tt]", WordMatchMethod.Regex));
    }

    [Fact]
    public void Regex_CharacterClass_MatchesEligibleTokens()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Equal(new[] { 4, 5 }, engine.Find("a[a-z]c", WordMatchMethod.Regex).OrderBy(x => x));
    }

    [Fact]
    public void Regex_Alternation_UnionPostingLists()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Equal(new[] { 2, 3 }, engine.Find("reporting|log", WordMatchMethod.Regex).OrderBy(x => x));
    }

    [Fact]
    public void Regex_InvalidPattern_ReturnsEmpty()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Empty(engine.Find("(unclosed", WordMatchMethod.Regex));
        Assert.Equal(0, engine.CountMatches("(unclosed", WordMatchMethod.Regex));
    }

    [Theory]
    [InlineData("foo(?=bar)", "foobar", 1)]
    [InlineData(@"(a)\1", "baad", 1)]
    public void Regex_NonBacktrackingUnsupportedConstruct_FallsBackToBacktracking(
        string pattern,
        string searchText,
        int expectedDocId)
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [expectedDocId] = searchText });

        Assert.Equal(new[] { expectedDocId }, engine.Find(pattern, WordMatchMethod.Regex));
        Assert.Equal(1, engine.CountMatches(pattern, WordMatchMethod.Regex));
    }

    [Fact]
    public void Regex_CatastrophicBacktrackingPattern_TimesOutToEmptyResult()
    {
        // (?=x) forces the backtracking fallback; (a+)+b against a long 'a' run
        // is the classic exponential-backtracking case and trips the 1 s timeout.
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = new string('a', 40) });

        Assert.Empty(engine.Find(@"(a+)+b(?=x)", WordMatchMethod.Regex));
        Assert.Equal(0, engine.CountMatches(@"(a+)+b(?=x)", WordMatchMethod.Regex));
    }

    [Fact]
    public void Regex_WithFacetFilter_IntersectsResults()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("reporting", "reporting", FacetValues.Create("size", 100)),
            [2] = new("log", "log", FacetValues.Create("size", 500)),
        });

        var filter = FacetFilter.Range("size", 200, long.MaxValue);
        Assert.Equal(new[] { 2 }, engine.Find("reporting|log", WordMatchMethod.Regex, false, SearchSortMode.SnapshotOrder, filter));
    }

    [Fact]
    public void Regex_NaturalSort_ReturnsSortedSubset()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [10] = "GA-100",
            [11] = "GA-10",
            [12] = "GA-2",
        });

        var results = engine.Find(
            "ga-.*",
            WordMatchMethod.Regex,
            false,
            SearchSortMode.NaturalSortAscending);

        Assert.Equal(new[] { 12, 11, 10 }, results);
    }

    [Fact]
    public void Regex_EmptyIndex_ReturnsEmpty()
    {
        var (engine, _) = CreateEngine();
        Assert.Empty(engine.Find(".*", WordMatchMethod.Regex));
        Assert.Equal(0, engine.CountMatches(".*", WordMatchMethod.Regex));
    }

    [Fact]
    public void Regex_IgnoresBooleanOperatorsInExpression()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Equal(new[] { 3 }, engine.Find("log", WordMatchMethod.Regex, enableOperators: true));
    }

    [Fact]
    public void Regex_FileMaskPreset_MatchesWholeNameToken()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "notes.txt",
            [3] = "my-report.pdf",
        });

        Assert.Equal(new[] { 1 }, engine.Find(@"^report-final\.pdf$", WordMatchMethod.Regex));
        Assert.Equal(new[] { 1, 2, 3 }, engine.Find(@"\.(pdf|txt)$", WordMatchMethod.Regex).OrderBy(x => x));
        Assert.Equal(new[] { 1, 3 }, engine.Find("report", WordMatchMethod.Regex).OrderBy(x => x));
    }

    [Fact]
    public void Regex_DoesNotMatchAcrossDefaultTokenBoundaries()
    {
        var (engine, _) = CreateTokenIndex();
        Assert.Empty(engine.Find(@"report.*\.pdf", WordMatchMethod.Regex));
    }
}
