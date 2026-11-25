using SearchEngine;

namespace SearchEngine.Sharp.Tests;

public class GlobMatchingTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateFileIndex()
    {
        var (engine, updater, _) = TestIndex.Create();
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

    [Test]
    public async Task StarAlone_MatchesAllDocuments()
    {
        var (engine, _) = CreateFileIndex();
        await Assert.That(engine.Find("*", WordMatchMethod.Exact).Count).IsEqualTo(8);
    }

    [Test]
    public async Task QuestionMarkAlone_MatchesSingleCharTokens()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "a b cd",
            [2] = "x y zzz",
        });

        var results = engine.Find("?", WordMatchMethod.Exact);
        await Assert.That(results.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PrefixGlob_MatchesLeadingPattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report*", WordMatchMethod.Exact);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SuffixGlob_MatchesTrailingPattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*log", WordMatchMethod.Exact);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 4, 7 }, CollectionOrdering.Matching);

        // Every token "*log" matches above is exactly "log", which a prefix test matches too: "report" ends with
        // "port" without starting with it.
        await Assert.That(engine.Find("*port", WordMatchMethod.Exact).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InfixGlob_MatchesMiddlePattern()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*report*", WordMatchMethod.Exact);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SingleCharWildcard_MatchesExactlyOneCharacter()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("a?c", WordMatchMethod.Exact);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 5, 6 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CollapsedStars_MatchLikeSingleStar()
    {
        var (engine, _) = CreateFileIndex();
        var single = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var collapsed = engine.Find("re**port*", WordMatchMethod.Exact).OrderBy(x => x);
        await Assert.That(collapsed).IsEquivalentTo(single, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComplexPattern_QuestionStarQuestion()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "axc",
            [2] = "abcd",
            [3] = "a123c",
        });

        var results = engine.Find("?*?", WordMatchMethod.Exact);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PatternLongerThanLongestWord_ReturnsEmpty()
    {
        var (engine, _) = CreateFileIndex();
        await Assert.That(engine.Find("zzzzzzzzzzzzzzzzzzzz", WordMatchMethod.Exact)).IsEmpty();
    }

    [Test]
    public async Task StarWrappedTerm_BehavesLikeWithinForSameLiteral()
    {
        var (engine, _) = CreateFileIndex();
        var within = engine.Find("abc", WordMatchMethod.Within).OrderBy(x => x);
        var glob = engine.Find("*abc*", WordMatchMethod.Exact).OrderBy(x => x);
        await Assert.That(glob).IsEquivalentTo(within, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Glob_IsCaseInsensitive()
    {
        var (engine, _) = CreateFileIndex();
        var lower = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var upper = engine.Find("REPORT*", WordMatchMethod.Exact).OrderBy(x => x);
        await Assert.That(upper).IsEquivalentTo(lower, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Glob_WorksInsideBooleanExpression()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report* AND NOT *tmp", WordMatchMethod.Exact, enableOperators: true);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Glob_WorksWithParenthesesAndImplicitAnd()
    {
        var (engine, _) = CreateFileIndex();
        var explicit_ = engine.Find("(report*) (log)", WordMatchMethod.Exact, enableOperators: true);
        var implicit_ = engine.Find("report* log", WordMatchMethod.Exact, enableOperators: true);
        await Assert.That(implicit_.OrderBy(x => x)).IsEquivalentTo(explicit_.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(implicit_).HasSingleItem();
        await Assert.That(implicit_[0]).IsEqualTo(7);
    }

    [Test]
    public async Task SingleTokenFastPathRegression_FooStar_DoesNotUseExactPostingShortcut()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("foo*", WordMatchMethod.Exact, enableOperators: false);
        await Assert.That(results).HasSingleItem();
        await Assert.That(results[0]).IsEqualTo(8);
    }

    [Test]
    public async Task Glob_RoutesRegardlessOfWordMatchMethod()
    {
        var (engine, _) = CreateFileIndex();
        var exact = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var within = engine.Find("report*", WordMatchMethod.Within).OrderBy(x => x);
        await Assert.That(within).IsEquivalentTo(exact, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ShapeFastPaths_AgreeWithGeneralMatcher_OnRandomPatterns()
    {
        var rng = new Random(123);
        const string patternAlphabet = "ab.**?";
        const string wordAlphabet = "ab.";
        for (int n = 0; n < 50_000; n++)
        {
            var pattern = new string(Enumerable.Range(0, rng.Next(1, 7)).Select(_ => patternAlphabet[rng.Next(patternAlphabet.Length)]).ToArray());
            var word = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => wordAlphabet[rng.Next(wordAlphabet.Length)]).ToArray());

            bool expected = SearchEngine.Query.GlobMatcher.IsWholeWordMatch(pattern, word);
            bool actual = new SearchEngine.Query.GlobMatcher.Shape(pattern).IsMatch(word);

            await Assert.That(actual).IsEqualTo(expected).Because($"pattern '{pattern}' word '{word}': expected {expected}");
        }
    }

    // The prefix and suffix shapes check code point boundaries without the boundary table; surrogate halves in both
    // alphabets make the general matcher reject a literal that ends or starts inside a pair.
    [Test]
    public async Task ShapeFastPaths_AgreeWithGeneralMatcher_OnSurrogatePairs()
    {
        var rng = new Random(321);
        const string patternAlphabet = "a😀**?";
        const string wordAlphabet = "a😀";
        for (int n = 0; n < 50_000; n++)
        {
            var pattern = new string(Enumerable.Range(0, rng.Next(1, 7)).Select(_ => patternAlphabet[rng.Next(patternAlphabet.Length)]).ToArray());
            var word = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => wordAlphabet[rng.Next(wordAlphabet.Length)]).ToArray());

            bool expected = SearchEngine.Query.GlobMatcher.IsWholeWordMatch(pattern, word);
            bool actual = new SearchEngine.Query.GlobMatcher.Shape(pattern).IsMatch(word);

            await Assert.That(actual).IsEqualTo(expected).Because($"pattern {Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(pattern))} word {Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(word))}");
        }
    }
}
