using SearchEngine;

namespace SearchEngine.Sharp.Tests;

public class GlobMatchingTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater);
    }

    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateFileIndex()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "report-draft.pdf",
            [3] = "tmp-cache.bin",
            [4] = "log.txt",
            [5] = "abc",
            [6] = "aXc",
            [7] = "archive-report.log",
            [8] = "foo-bar",
        });
        return (engine, updater);
    }

    [Fact]
    public void StarAlone_MatchesAllDocuments()
    {
        var (engine, _) = CreateFileIndex();
        Assert.Equal(8, engine.Find("*", WordMatchMethod.Exact).Count);
    }

    [Fact]
    public void QuestionMarkAlone_MatchesSingleCharTokens()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "a b cd",
            [2] = "x y zzz",
        });

        var results = engine.Find("?", WordMatchMethod.Exact);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void PrefixGlob_MatchesLeadingPattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report*", WordMatchMethod.Exact);
        Assert.Equal(new[] { 1, 2, 7 }, results.OrderBy(x => x));
    }

    [Fact]
    public void SuffixGlob_MatchesTrailingPattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*log", WordMatchMethod.Exact);
        Assert.Equal(new[] { 4, 7 }, results.OrderBy(x => x));
    }

    [Fact]
    public void InfixGlob_MatchesMiddlePattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*report*", WordMatchMethod.Exact);
        Assert.Equal(new[] { 1, 2, 7 }, results.OrderBy(x => x));
    }

    [Fact]
    public void SingleCharWildcard_MatchesExactlyOneCharacter()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("a?c", WordMatchMethod.Exact);
        Assert.Equal(new[] { 5, 6 }, results.OrderBy(x => x));
    }

    [Fact]
    public void CollapsedStars_MatchLikeSingleStar()
    {
        var (engine, _) = CreateFileIndex();
        var single = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var collapsed = engine.Find("re**port*", WordMatchMethod.Exact).OrderBy(x => x);
        Assert.Equal(single, collapsed);
    }

    [Fact]
    public void ComplexPattern_QuestionStarQuestion()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "axc",
            [2] = "abcd",
            [3] = "a123c",
        });

        var results = engine.Find("?*?", WordMatchMethod.Exact);
        Assert.Equal(new[] { 1, 2, 3 }, results.OrderBy(x => x));
    }

    [Fact]
    public void PatternLongerThanLongestWord_ReturnsEmpty()
    {
        var (engine, _) = CreateFileIndex();
        Assert.Empty(engine.Find("zzzzzzzzzzzzzzzzzzzz", WordMatchMethod.Exact));
    }

    [Fact]
    public void StarWrappedTerm_BehavesLikeWithinForSameLiteral()
    {
        var (engine, _) = CreateFileIndex();
        var within = engine.Find("abc", WordMatchMethod.Within).OrderBy(x => x);
        var glob = engine.Find("*abc*", WordMatchMethod.Exact).OrderBy(x => x);
        Assert.Equal(within, glob);
    }

    [Fact]
    public void Glob_IsCaseInsensitive()
    {
        var (engine, _) = CreateFileIndex();
        var lower = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var upper = engine.Find("REPORT*", WordMatchMethod.Exact).OrderBy(x => x);
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Glob_WorksInsideBooleanExpression()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report* AND NOT *tmp", WordMatchMethod.Exact, enableOperators: true);
        Assert.Equal(new[] { 1, 2, 7 }, results.OrderBy(x => x));
    }

    [Fact]
    public void Glob_WorksWithParenthesesAndImplicitAnd()
    {
        var (engine, _) = CreateFileIndex();
        var explicit_ = engine.Find("(report*) (log)", WordMatchMethod.Exact, enableOperators: true);
        var implicit_ = engine.Find("report* log", WordMatchMethod.Exact, enableOperators: true);
        Assert.Equal(explicit_.OrderBy(x => x), implicit_.OrderBy(x => x));
        Assert.Single(implicit_);
        Assert.Equal(7, implicit_[0]);
    }

    [Fact]
    public void SingleTokenFastPathRegression_FooStar_DoesNotUseExactPostingShortcut()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("foo*", WordMatchMethod.Exact, enableOperators: false);
        Assert.Single(results);
        Assert.Equal(8, results[0]);
    }

    [Fact]
    public void Glob_RoutesRegardlessOfWordMatchMethod()
    {
        var (engine, _) = CreateFileIndex();
        var exact = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var within = engine.Find("report*", WordMatchMethod.Within).OrderBy(x => x);
        Assert.Equal(exact, within);
    }
}
