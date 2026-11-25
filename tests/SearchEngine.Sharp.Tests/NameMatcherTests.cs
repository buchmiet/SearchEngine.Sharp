namespace SearchEngine.Sharp.Tests;

public class NameMatcherTests
{
    private static readonly string[] Names = BuildNames();

    private static string[] BuildNames()
    {
        var rng = new Random(77);
        string[] stems = ["Report", "report-final", "IMG_0001", "invoice 2024", "Zażółć gęślą", "Zaz\u0307o\u0301\u0142c\u0301 ge\u0328s\u0301la\u0328", "Re\u0301sume\u0301", "Straße", "readme", "a.b-c", "(draft) notes", "ΟΔΥΣΣΕΥΣ", "x"];
        string[] exts = [".pdf", ".PDF", ".jpg", ".txt", "", ".tar.gz", ".cs"];
        var names = new List<string>();
        for (int i = 0; i < 400; i++)
        {
            string name = stems[rng.Next(stems.Length)] + (rng.Next(3) == 0 ? " " + rng.Next(100) : "") + exts[rng.Next(exts.Length)];
            names.Add(name);
        }

        return [.. names];
    }

    public static TheoryData<string, WordMatchMethod, bool> Queries() => new()
    {
        { "*.pdf", WordMatchMethod.Exact, true },
        { "report*", WordMatchMethod.Exact, true },
        { "*port*", WordMatchMethod.Exact, false },
        { "report", WordMatchMethod.Exact, false },
        { "report", WordMatchMethod.Within, false },
        { "img_????.jpg", WordMatchMethod.Exact, true },
        { "*.jpg OR *.pdf", WordMatchMethod.Exact, true },
        { "*.cs AND NOT x*", WordMatchMethod.Exact, true },
        { "NOT *.txt", WordMatchMethod.Exact, true },
        { "(report OR invoice) AND NOT final", WordMatchMethod.Within, true },
        { "NOT AND *.pdf", WordMatchMethod.Exact, true },
        { "pdf invoice", WordMatchMethod.Exact, false },
        { "re", WordMatchMethod.Within, true },
        { "zażółć*", WordMatchMethod.Exact, true },
        { "strasse*", WordMatchMethod.Exact, true },
        { @"\.(jpg|pdf)$", WordMatchMethod.Regex, false },
        { "^rep", WordMatchMethod.Regex, false },
        { "[invalid", WordMatchMethod.Regex, false },
        { "*", WordMatchMethod.Exact, true },
        { "   ", WordMatchMethod.Exact, true },
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void IsMatch_AgreesWithFindOnAnIndex_ForBothPresetsAndUnicodeOptions(string query, WordMatchMethod method, bool operators)
    {
        SearchTokenization[] presets =
        [
            SearchTokenization.Default,
            SearchTokenization.FileMask,
            SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries),
            .. TextFolder.IsNormalizationAvailable
                ? (SearchTokenization[])
                [
                    SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
                    SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics | UnicodeMatching.GraphemeBoundaries),
                ]
                : [],
        ];

        foreach (var preset in presets)
        {
            var provider = new IndexSnapshotProvider();
            new IndexUpdater(provider, preset).RebuildFrom(Names.Select((n, i) => (n, i)).ToDictionary(x => x.i, x => x.n));
            var expected = new SearchEngineSharp(provider).Find(query, method, operators).Order().ToList();

            var matcher = NameMatcher.Create(query, method, operators, preset);
            var actual = Enumerable.Range(0, Names.Length).Where(i => matcher.IsMatch(Names[i])).ToList();

            Assert.True(expected.SequenceEqual(actual),
                $"{query} / {method} / ops={operators} / {preset.IndexSeparators.Length}:{preset.UnicodeMatching}: index {expected.Count}, matcher {actual.Count}");
        }
    }

    // NameMatcher.IsMatch(text) must equal "Find on an index holding exactly that text returns it", for every
    // combination of text, query, method, operators and tokenization below. Mismatches are collected and reported
    // together.
    private static readonly string[] ParityTexts =
    [
        "", "   ", "alpha beta", "alpha", "gamma", "aXb", "axb", "a-b.c", "report-final.pdf", "(draft) notes.txt",
        "IMG_0001.JPG", "AND or Not", "aaa aab abb",
        "Stra\u00DFe STRA\u1E9EE.pdf", "k \u212A", "\u03C2 \u03C3",
        "\u039F\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2 \u039F\u0394\u03A5\u03A3\u03A3\u0395\u03A5\u03A3.txt",
        "\u00B5 \u03BC", "\u03B2 \u03D0", "cafe\u0301 caf\u00E9",
    ];

    private static readonly string[] ParityQueries =
    [
        "*", "?", "NOT foo", "foo", "a", "x", "b", "alpha", "alpha beta", "alpha OR gamma", "NOT alpha",
        "alpha NOT AND beta", "(AND alpha)", "gamma OR (alpha NOT AND beta)", "()", "(alpha OR gamma) AND NOT beta",
        "rep*", "*.pdf", "*x*", "a*b", "a?b", "notes", "and", "not", "stra\u00DFe", "k", "\u03C3", "\u03C2",
        "\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C3", "\u03BC", "\u03D0", "caf", "\u00E9",
    ];

    private static readonly string[] ParityPatterns =
    [
        "a", "^a$", "^$", "[invalid", "x", "b$", "^a.b$", "\u03C3", "e\u0301", @".pdf$",
    ];

    // Compose and RemoveDiacritics need Unicode normalization; in globalization-invariant mode the parity check runs
    // without them.
    private static readonly SearchTokenization[] ParityTokenizations =
    [
        SearchTokenization.Default,
        SearchTokenization.FileMask,
        SearchTokenization.Create("X", " "),
        SearchTokenization.Create("x", " "),
        SearchTokenization.Create("-", " ()"),
        SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold),
        .. TextFolder.IsNormalizationAvailable
            ? (SearchTokenization[])
            [
                SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
                SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics | UnicodeMatching.GraphemeBoundaries),
            ]
            : [],
    ];

    [Fact]
    public void IsMatch_AgreesWithFindOnAOneEntryIndex()
    {
        var mismatches = new List<string>();
        int checkedCount = 0;
        foreach (var tokenization in ParityTokenizations)
        {
            foreach (var text in ParityTexts)
            {
                var provider = new IndexSnapshotProvider();
                new IndexUpdater(provider, tokenization).RebuildFrom(new Dictionary<int, string> { [1] = text });
                var engine = new SearchEngineSharp(provider);

                void Check(string query, WordMatchMethod method, bool operators)
                {
                    bool expected = engine.Find(query, method, operators).Contains(1);
                    bool actual = NameMatcher.Create(query, method, operators, tokenization).IsMatch(text);
                    checkedCount++;
                    if (expected != actual)
                    {
                        mismatches.Add($"index '{tokenization.IndexSeparators}' query '{tokenization.QuerySeparators}' "
                            + $"{tokenization.UnicodeMatching} | text \"{text}\" | {method} ops={operators} \"{query}\": "
                            + $"index {expected}, matcher {actual}");
                    }
                }

                foreach (var query in ParityQueries)
                {
                    foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
                    {
                        Check(query, method, operators: false);
                        Check(query, method, operators: true);
                    }
                }

                foreach (var pattern in ParityPatterns)
                    Check(pattern, WordMatchMethod.Regex, operators: false);
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {checkedCount} combinations differ:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void IsMatch_RegexTimeoutOnAnyToken_IsNoMatch_LikeTheIndex()
    {
        // The lookahead forces the backtracking engine (1 s timeout). "b" matches at once; the second token makes
        // the nested quantifier backtrack exponentially and time out. The index then returns nothing for the
        // whole query, so the matcher must not report the earlier hit.
        const string pattern = "(?=b)|^(a+)+$";
        string text = "b " + new string('a', 40) + "!";
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(new Dictionary<int, string> { [1] = text });

        Assert.Empty(new SearchEngineSharp(provider).Find(pattern, WordMatchMethod.Regex));
        Assert.False(NameMatcher.Create(pattern, WordMatchMethod.Regex).IsMatch(text));
    }
}
