using System.Globalization;

namespace SearchEngine.Sharp.Tests;

// A glob '?' matches one unit of user text: one code point (a surrogate pair is one, a lone surrogate is one), or one
// extended grapheme cluster with UnicodeMatching.GraphemeBoundaries. '*' covers whole units, and every place where a
// wildcard meets literal text is a unit boundary (docs/query-semantics.md, globs). A user who types ? for one character
// must not split an emoji or an accented letter in two. GlobMatcher and NameMatcher apply the rule; a brute-force
// matcher over the same boundaries is the reference.
public class GlobTextUnitTests
{
    private const string Grin = "\uD83D\uDE00";
    private const string CafeDecomposed = "cafe\u0301";
    private const string Family = "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67";

    public static IEnumerable<Func<(bool GraphemeBoundaries, string Pattern, string Name, bool Expected)>> Cases() =>
    [
        () => (false, "?.txt", Grin + ".txt", true),
        () => (false, "??.txt", Grin + ".txt", false),
        () => (false, "caf?.txt", CafeDecomposed + ".txt", false),
        () => (false, "caf??.txt", CafeDecomposed + ".txt", true),
        () => (false, "?", "\uD800", true),
        () => (false, "a?b", "a\uDC00b", true),
        () => (false, "a?b", "a\uD800\uDC00b", true),
        () => (false, "a??b", "a\uD800\uDC00b", false),
        () => (false, "*\uDC00", "\uD800\uDC00", false),
        () => (false, "?????", Family, true),
        () => (true, "?.txt", Grin + ".txt", true),
        () => (true, "caf?.txt", CafeDecomposed + ".txt", true),
        () => (true, "caf??.txt", CafeDecomposed + ".txt", false),
        () => (true, "?", Family, true),
        () => (true, "??", Family, false),
        () => (true, "?x", "e\u0301x", true),
        () => (true, "*?", Family, true),
        () => (true, "?*?", Family, false),
    ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task A_question_mark_matches_one_unit_of_text(bool graphemeBoundaries, string pattern, string name, bool expected)
    {
        var tokenization = graphemeBoundaries
            ? SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries)
            : SearchTokenization.FileMask;
        var (engine, updater, _) = TestIndex.Create(tokenization);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = name });
        int[] ids = expected ? [1] : [];

        await Assert.That(engine.Find(pattern, WordMatchMethod.Exact)).IsEquivalentTo(ids, CollectionOrdering.Matching);
        await Assert.That(NameMatcher.Create(pattern, WordMatchMethod.Exact, tokenization: tokenization).IsMatch(name)).IsEqualTo(expected);
    }

    private static readonly string[] Pool =
    [
        "a", "b", "e", ".", "\u0301", "\u0308", Grin, "\uD83C\uDFFD", "\u200D", "\uD800", "\uDC00", "\uD83C\uDDF5", "\uD83C\uDDF1",
        "\u0E01", "\u0E34", "\u1100", "\u1161",
    ];

    // Index, NameMatcher and a brute-force matcher agree on generated names and patterns: every glob shape (prefix,
    // suffix, contains, prefix and suffix, general) and the length prefilters of the index scan are exercised.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Index_and_NameMatcher_agree_with_a_brute_force_glob_matcher(bool graphemeBoundaries)
    {
        var random = new Random(graphemeBoundaries ? 8 : 9);
        var names = new Dictionary<int, string>();
        for (int id = 1; id <= 400; id++)
            names[id] = string.Concat(Enumerable.Range(0, random.Next(1, 7)).Select(_ => Pool[random.Next(Pool.Length)]));

        var tokenization = graphemeBoundaries
            ? SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries)
            : SearchTokenization.FileMask;
        var (engine, updater, _) = TestIndex.Create(tokenization);
        updater.RebuildFrom(names);

        var failures = new List<string>();
        for (int i = 0; i < 300; i++)
        {
            string pattern = RandomPattern(random, names[random.Next(1, names.Count + 1)]);
            int[] expected = [.. names.Where(n => Reference(pattern, n.Value, graphemeBoundaries)).Select(n => n.Key).Order()];
            int[] index = [.. engine.Find(pattern, WordMatchMethod.Exact).Order()];
            var matcher = NameMatcher.Create(pattern, WordMatchMethod.Exact, tokenization: tokenization);
            int[] matched = [.. names.Where(n => matcher.IsMatch(n.Value)).Select(n => n.Key).Order()];
            if (!index.SequenceEqual(expected) || !matched.SequenceEqual(expected))
                failures.Add($"{Escape(pattern)}: expected {expected.Length}, index {index.Length}, matcher {matched.Length}");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures.Take(20)));
    }

    // A name with some characters replaced by '?' and some runs by '*', or a pattern drawn from the pool.
    private static string RandomPattern(Random random, string name)
    {
        if (random.Next(4) == 0)
        {
            return string.Concat(Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => random.Next(3) switch { 0 => "*", 1 => "?", _ => Pool[random.Next(Pool.Length)] }));
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            switch (random.Next(6))
            {
                case 0: sb.Append('?'); break;
                case 1: sb.Append('*'); i += random.Next(3); break;
                default: sb.Append(name[i]); break;
            }
        }

        string pattern = sb.ToString();
        return pattern.AsSpan().ContainsAny('*', '?') ? pattern : "*" + pattern;
    }

    // Unit boundaries: cluster starts (UAX #29) with GraphemeBoundaries, otherwise every position that does not
    // split a surrogate pair.
    private static bool[] UnitBoundaries(string word, bool graphemeBoundaries)
    {
        var boundaries = new bool[word.Length + 1];
        boundaries[word.Length] = true;
        if (graphemeBoundaries)
        {
            foreach (int start in StringInfo.ParseCombiningCharacters(word))
                boundaries[start] = true;
        }
        else
        {
            for (int i = 0; i < word.Length; i++)
                boundaries[i] = i == 0 || !(char.IsHighSurrogate(word[i - 1]) && char.IsLowSurrogate(word[i]));
        }

        return boundaries;
    }

    // Matches the whole word: '*' takes any number of whole units, '?' one unit, a literal its UTF-16 unit; a
    // wildcard starts only on a unit boundary.
    private static bool Reference(string pattern, string word, bool graphemeBoundaries)
    {
        bool[] boundary = UnitBoundaries(word, graphemeBoundaries);
        var memo = new Dictionary<(int, int), bool>();

        bool Match(int p, int w)
        {
            if (p == pattern.Length)
                return w == word.Length;

            if (memo.TryGetValue((p, w), out bool known))
                return known;

            bool result;
            switch (pattern[p])
            {
                case '*':
                    result = false;
                    if (boundary[w])
                    {
                        for (int end = w; end <= word.Length && !result; end++)
                            result = boundary[end] && Match(p + 1, end);
                    }

                    break;
                case '?':
                    int next = w + 1;
                    while (next < word.Length && !boundary[next])
                        next++;
                    result = w < word.Length && boundary[w] && Match(p + 1, next);
                    break;
                default:
                    result = w < word.Length && word[w] == pattern[p] && Match(p + 1, w + 1);
                    break;
            }

            memo[(p, w)] = result;
            return result;
        }

        return Match(0, 0);
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:X4}"));
}
