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
}
