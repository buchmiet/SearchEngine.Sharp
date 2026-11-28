using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

// A term with * or ? is a glob matched against whole tokens (docs/query-semantics.md, globs; src/Query/GlobMatcher.cs):
// * takes any run, ? one character, repeated stars act as one, case is ignored, and a glob works inside expressions and
// under either WordMatchMethod. Prefix, suffix, infix and prefix-and-suffix patterns take Shape fast paths that must
// agree with the general matcher. Which unit of text a wildcard takes (code point or grapheme cluster) is in
// GlobTextUnitTests; the single-term shortcut that a glob must bypass is in SingleTermFastPathTests.
public class GlobMatchingTests
{
    // 1: report-final.pdf, 2: report-draft.pdf, 3: tmp-cache.bin, 4: log.txt, 5: abc, 6: aXc, 7: archive-report.log,
    // 8: foo-bar
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

    // A scan of the tokens would miss the entries without any: "---" splits into nothing and "" is empty.
    [Test]
    [Arguments("*")]
    [Arguments("**")]
    public async Task A_term_of_only_stars_matches_every_entry_even_one_without_tokens(string query)
    {
        string[] names = ["report-final.pdf", "---", ""];
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string> { [1] = names[0], [2] = names[1], [3] = names[2] });
        var matcher = NameMatcher.Create(query, WordMatchMethod.Exact);

        var results = engine.Find(query, WordMatchMethod.Exact);
        int count = engine.CountMatches(query, WordMatchMethod.Exact);

        await Assert.That(results.Order()).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(names.All(name => matcher.IsMatch(name))).IsTrue();
    }

    [Test]
    public async Task A_lone_question_mark_matches_one_character_tokens()
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
    public async Task A_prefix_glob_matches_tokens_that_start_with_the_text()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report*", WordMatchMethod.Exact);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_suffix_glob_matches_tokens_that_end_with_the_text()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*log", WordMatchMethod.Exact);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 4, 7 }, CollectionOrdering.Matching);

        // Every token "*log" matches above is exactly "log", which a prefix test matches too: "report" ends with
        // "port" without starting with it.
        await Assert.That(engine.Find("*port", WordMatchMethod.Exact).OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_infix_glob_matches_tokens_that_contain_the_text()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("*report*", WordMatchMethod.Exact);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_question_mark_matches_exactly_one_character()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("a?c", WordMatchMethod.Exact);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 5, 6 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Repeated_stars_match_like_one_star()
    {
        var (engine, _) = CreateFileIndex();
        var single = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var collapsed = engine.Find("re**port*", WordMatchMethod.Exact).OrderBy(x => x);

        await Assert.That(collapsed).IsEquivalentTo(single, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Question_star_question_matches_tokens_of_two_or_more_characters()
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
    public async Task A_star_wrapped_term_matches_like_Within()
    {
        var (engine, _) = CreateFileIndex();
        var within = engine.Find("abc", WordMatchMethod.Within).OrderBy(x => x);
        var glob = engine.Find("*abc*", WordMatchMethod.Exact).OrderBy(x => x);

        await Assert.That(glob).IsEquivalentTo(within, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Globs_ignore_case()
    {
        var (engine, _) = CreateFileIndex();
        var lower = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var upper = engine.Find("REPORT*", WordMatchMethod.Exact).OrderBy(x => x);

        await Assert.That(upper).IsEquivalentTo(lower, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_glob_works_inside_a_boolean_expression()
    {
        var (engine, _) = CreateFileIndex();
        var results = engine.Find("report* AND NOT *tmp", WordMatchMethod.Exact, enableOperators: true);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_glob_works_in_parentheses_and_with_implicit_AND()
    {
        var (engine, _) = CreateFileIndex();
        var explicit_ = engine.Find("(report*) (log)", WordMatchMethod.Exact, enableOperators: true);
        var implicit_ = engine.Find("report* log", WordMatchMethod.Exact, enableOperators: true);

        await Assert.That(implicit_.OrderBy(x => x)).IsEquivalentTo(explicit_.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(implicit_).HasSingleItem();
        await Assert.That(implicit_[0]).IsEqualTo(7);
    }

    [Test]
    public async Task A_glob_matches_the_same_under_Exact_and_Within()
    {
        var (engine, _) = CreateFileIndex();
        var exact = engine.Find("report*", WordMatchMethod.Exact).OrderBy(x => x);
        var within = engine.Find("report*", WordMatchMethod.Within).OrderBy(x => x);

        await Assert.That(within).IsEquivalentTo(exact, CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_shape_fast_paths_agree_with_the_general_matcher_on_random_patterns()
    {
        var rng = new Random(123);
        const string patternAlphabet = "ab.**?";
        const string wordAlphabet = "ab.";
        var failures = new List<string>();

        for (int n = 0; n < 50_000; n++)
        {
            var pattern = new string(Enumerable.Range(0, rng.Next(1, 7)).Select(_ => patternAlphabet[rng.Next(patternAlphabet.Length)]).ToArray());
            var word = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => wordAlphabet[rng.Next(wordAlphabet.Length)]).ToArray());
            bool expected = GlobMatcher.IsWholeWordMatch(pattern, word);
            if (new GlobMatcher.Shape(pattern).IsMatch(word) != expected)
                failures.Add($"pattern '{pattern}' word '{word}': expected {expected}");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures.Take(20)));
    }

    // The prefix and suffix shapes check code point boundaries without the boundary table; surrogate halves in both
    // alphabets make the general matcher reject a literal that ends or starts inside a pair.
    [Test]
    public async Task The_shape_fast_paths_agree_with_the_general_matcher_on_surrogate_pairs()
    {
        var rng = new Random(321);
        const string patternAlphabet = "a😀**?";
        const string wordAlphabet = "a😀";
        var failures = new List<string>();

        for (int n = 0; n < 50_000; n++)
        {
            var pattern = new string(Enumerable.Range(0, rng.Next(1, 7)).Select(_ => patternAlphabet[rng.Next(patternAlphabet.Length)]).ToArray());
            var word = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => wordAlphabet[rng.Next(wordAlphabet.Length)]).ToArray());
            bool expected = GlobMatcher.IsWholeWordMatch(pattern, word);
            if (new GlobMatcher.Shape(pattern).IsMatch(word) != expected)
                failures.Add($"pattern {Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(pattern))} word {Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(word))}: expected {expected}");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures.Take(20)));
    }
}
