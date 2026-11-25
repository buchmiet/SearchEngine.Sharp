using System.Text;

namespace SearchEngine.Sharp.Tests;

public class UnicodeMatchingTests
{
    private static SearchEngineSharp Index(SearchTokenization tokenization, params string[] names)
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, tokenization);
        var entries = new Dictionary<int, string>();
        for (int i = 0; i < names.Length; i++) entries[i + 1] = names[i];
        updater.RebuildFrom(entries);
        return new SearchEngineSharp(provider);
    }

    private static string Nfd(string s) => s.Normalize(NormalizationForm.FormD);

    [Fact]
    public void Default_DecomposedNameIsNotFoundByComposedQuery()
    {
        var engine = Index(SearchTokenization.FileMask, Nfd("Zażółć.txt"));

        Assert.Empty(engine.Find("zażółć*", WordMatchMethod.Exact, enableOperators: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_MatchesAcrossNfcAndNfd(bool nameDecomposed)
    {
        string name = nameDecomposed ? Nfd("Zażółć.txt") : "Zażółć.txt";
        string query = nameDecomposed ? "zażółć*" : Nfd("zażółć*");
        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose), name, "other.txt");

        Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([1], engine.Find(Nfd("zażółć.txt"), WordMatchMethod.Exact, enableOperators: true));
    }

    [Theory]
    [InlineData("Straße.txt", "strasse*")]
    [InlineData("STRASSE.txt", "straße*")]
    [InlineData("Οδυσσευς.txt", "ΟΔΥΣΣΕΥΣ*")]
    [InlineData("İstanbul.txt", "istanbul*")]
    [InlineData("ﬁle.txt", "file*")]
    public void CaseFold_FoldsCharactersThatLowercaseDoesNot(string name, string query)
    {
        var plain = Index(SearchTokenization.FileMask, name);
        var folded = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold), name);

        Assert.Empty(plain.Find(query, WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([1], folded.Find(query, WordMatchMethod.Exact, enableOperators: true));
    }

    [Theory]
    [InlineData("Łódź zdjęcia.jpg", "lodz")]
    [InlineData("café crème.txt", "creme")]
    [InlineData("Ærø Øresund.txt", "oresund")]
    public void RemoveDiacritics_MatchesUnaccentedQuery_DefaultPreset(string name, string query)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), Nfd(name));

        Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact));
        Assert.Equal([1], engine.Find(query.ToUpperInvariant(), WordMatchMethod.Within));
    }

    [Fact]
    public void GraphemeBoundaries_WithinDoesNotMatchInsideAGraphemeCluster()
    {
        string family = "👨‍👩‍👧";
        string[] names = [Nfd("café"), family, "👨 solo", "cafe"];
        var plain = Index(SearchTokenization.Default, names);
        var bounded = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries), names);

        Assert.Equal([1, 4], plain.Find("cafe", WordMatchMethod.Within));
        Assert.Equal([4], bounded.Find("cafe", WordMatchMethod.Within));
        Assert.Equal([2, 3], plain.Find("👨", WordMatchMethod.Within));
        Assert.Equal([3], bounded.Find("👨", WordMatchMethod.Within));
        Assert.Empty(bounded.Find("👩", WordMatchMethod.Within));
    }

    // Lone surrogates (legal in NTFS names) and U+FFFE make string.Normalize throw; folding must not.
    [Theory]
    [InlineData(UnicodeMatching.Compose)]
    [InlineData(UnicodeMatching.RemoveDiacritics)]
    [InlineData(UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries)]
    public void IllFormedText_IsIndexedAndMatchesItself(UnicodeMatching options)
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(options);
        string[] names = ["ok.txt", "report\uD800.txt", "\uDC00cafe\u0301.txt", "a\uFFFEb.txt", "zdj\uD83D\uDE00.jpg"];
        var engine = Index(tokenization, names);

        Assert.Equal([1], engine.Find("ok.txt", WordMatchMethod.Exact));
        Assert.Equal([2], engine.Find("report\uD800.txt", WordMatchMethod.Exact));
        Assert.Equal([2], engine.Find("report\uD800*", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([3], engine.Find("\uDC00caf\u00E9*", WordMatchMethod.Exact));
        Assert.Equal([4], engine.Find("a\uFFFEb.txt", WordMatchMethod.Exact));
        Assert.Equal(1, engine.CountMatches("*\uFFFE*", WordMatchMethod.Exact));
        Assert.Empty(engine.Find("zdj\uD83D", WordMatchMethod.Exact));
        Assert.Equal([5], engine.Find("zdj\uD83D\uDE00*", WordMatchMethod.Exact));

        foreach (string name in names)
            Assert.True(NameMatcher.Create(name, WordMatchMethod.Exact, tokenization: tokenization).IsMatch(name));
        Assert.False(NameMatcher.Create("zdj\uD83D", WordMatchMethod.Exact, tokenization: tokenization).IsMatch(names[4]));
    }

    [Fact]
    public void IllFormedText_FoldsTheWellFormedRunsAndKeepsTheRest()
    {
        const UnicodeMatching all = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;

        Assert.Equal("\uD800e\uDC00a\uFFFEss", TextFolder.Fold("\uD800e\u0301\uDC00a\u030A\uFFFE\u00DF", all, isRegex: false));
        Assert.Equal("\uD800\u00E9\uDC00\uFFFE", TextFolder.Fold("\uD800e\u0301\uDC00\uFFFE", UnicodeMatching.Compose, isRegex: false));
    }

    [Fact]
    public void RegexPattern_IsComposedButNotCaseFolded()
    {
        var engine = Index(
            SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
            Nfd("Résumé.pdf"),
            "Straße.pdf");

        Assert.Equal([1], engine.Find(Nfd("^résumé"), WordMatchMethod.Regex));
        Assert.Equal([2], engine.Find(@"^strasse\.pdf$", WordMatchMethod.Regex));
        Assert.Empty(engine.Find(@"^straße\.pdf$", WordMatchMethod.Regex));
    }
}
