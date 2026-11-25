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

    [Test]
    public async Task Regex_UnanchoredMatch_MatchesSubstringWithinToken()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("^reporting$", WordMatchMethod.Regex)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
        var reportMatches = engine.Find("report", WordMatchMethod.Regex).OrderBy(x => x);
        await Assert.That(reportMatches).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_PrefixPattern_MatchesTokensContainingPrefix()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("report.*", WordMatchMethod.Regex).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_IgnoreCase_MatchesNormalizedTokens()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "Report-Final.PDF" });

        await Assert.That(engine.Find("REPORT", WordMatchMethod.Regex)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("[Rr][Ee][Pp][Oo][Rr][Tt]", WordMatchMethod.Regex)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_CharacterClass_MatchesEligibleTokens()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("a[a-z]c", WordMatchMethod.Regex).OrderBy(x => x)).IsEquivalentTo(new[] { 4, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_Alternation_UnionPostingLists()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("reporting|log", WordMatchMethod.Regex).OrderBy(x => x)).IsEquivalentTo(new[] { 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_InvalidPattern_ReturnsEmpty()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("(unclosed", WordMatchMethod.Regex)).IsEmpty();
        await Assert.That(engine.CountMatches("(unclosed", WordMatchMethod.Regex)).IsEqualTo(0);
    }

    [Test]
    [Arguments("foo(?=bar)", "foobar", 1)]
    [Arguments(@"(a)\1", "baad", 1)]
    public async Task Regex_NonBacktrackingUnsupportedConstruct_FallsBackToBacktracking(
        string pattern,
        string searchText,
        int expectedDocId)
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [expectedDocId] = searchText });

        await Assert.That(engine.Find(pattern, WordMatchMethod.Regex)).IsEquivalentTo(new[] { expectedDocId }, CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches(pattern, WordMatchMethod.Regex)).IsEqualTo(1);
    }

    [Test]
    public async Task Regex_CatastrophicBacktrackingPattern_TimesOutToEmptyResult()
    {
        // (?=x) forces the backtracking fallback; (a+)+b against a long 'a' run
        // is the classic exponential-backtracking case and trips the 1 s timeout.
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = new string('a', 40) });

        await Assert.That(engine.Find(@"(a+)+b(?=x)", WordMatchMethod.Regex)).IsEmpty();
        await Assert.That(engine.CountMatches(@"(a+)+b(?=x)", WordMatchMethod.Regex)).IsEqualTo(0);
    }

    [Test]
    public async Task Regex_WithFacetFilter_IntersectsResults()
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
        await Assert.That(engine.Find("reporting|log", WordMatchMethod.Regex, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_NaturalSort_ReturnsSortedSubset()
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

        await Assert.That(results).IsEquivalentTo(new[] { 12, 11, 10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_EmptyIndex_ReturnsEmpty()
    {
        var (engine, _) = CreateEngine();
        await Assert.That(engine.Find(".*", WordMatchMethod.Regex)).IsEmpty();
        await Assert.That(engine.CountMatches(".*", WordMatchMethod.Regex)).IsEqualTo(0);
    }

    [Test]
    public async Task Regex_IgnoresBooleanOperatorsInExpression()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find("log", WordMatchMethod.Regex, enableOperators: true)).IsEquivalentTo(new[] { 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_FileMaskPreset_MatchesWholeNameToken()
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

        await Assert.That(engine.Find(@"^report-final\.pdf$", WordMatchMethod.Regex)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find(@"\.(pdf|txt)$", WordMatchMethod.Regex).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("report", WordMatchMethod.Regex).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Regex_DoesNotMatchAcrossDefaultTokenBoundaries()
    {
        var (engine, _) = CreateTokenIndex();
        await Assert.That(engine.Find(@"report.*\.pdf", WordMatchMethod.Regex)).IsEmpty();
    }
}
