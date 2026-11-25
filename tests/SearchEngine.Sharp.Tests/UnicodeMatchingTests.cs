using System.Globalization;

namespace SearchEngine.Sharp.Tests;

// Decomposed (NFD) strings are written with explicit escapes: a helper that calls string.Normalize would return its
// input unchanged under globalization-invariant mode and make the tests vacuous there.
public class UnicodeMatchingTests
{
    private const string ZazolcNfc = "Za\u017C\u00F3\u0142\u0107";
    private const string ZazolcNfd = "Zaz\u0307o\u0301\u0142c\u0301";
    private const string Man = "\uD83D\uDC68";
    private const string Woman = "\uD83D\uDC69";
    private const string Family = "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67";

    private static SearchEngineSharp Index(SearchTokenization tokenization, params string[] names)
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, tokenization);
        var entries = new Dictionary<int, string>();
        for (int i = 0; i < names.Length; i++) entries[i + 1] = names[i];
        updater.RebuildFrom(entries);
        return new SearchEngineSharp(provider);
    }

    [Fact]
    public void Default_DecomposedNameIsNotFoundByComposedQuery()
    {
        var engine = Index(SearchTokenization.FileMask, ZazolcNfd + ".txt");

        Assert.Empty(engine.Find(ZazolcNfc.ToLowerInvariant() + "*", WordMatchMethod.Exact, enableOperators: true));
    }

    [NormalizationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_MatchesAcrossNfcAndNfd(bool nameDecomposed)
    {
        string name = (nameDecomposed ? ZazolcNfd : ZazolcNfc) + ".txt";
        string query = (nameDecomposed ? ZazolcNfc : ZazolcNfd) + "*";
        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose), name, "other.txt");

        Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([1], engine.Find(ZazolcNfd + ".txt", WordMatchMethod.Exact, enableOperators: true));
    }

    // Compose and RemoveDiacritics need Unicode normalization, which globalization-invariant mode
    // (InvariantGlobalization, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1) turns into a no-op. Asking for them there
    // fails loudly instead of matching nothing. Run the suite once with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 to
    // cover the second branch.
    [Theory]
    [InlineData(UnicodeMatching.Compose)]
    [InlineData(UnicodeMatching.RemoveDiacritics)]
    [InlineData(UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries)]
    public void NormalizingOptions_WorkWithICU_AndThrowInInvariantGlobalizationMode(UnicodeMatching options)
    {
        if (IsInvariantGlobalizationMode())
        {
            Assert.Throws<PlatformNotSupportedException>(() => SearchTokenization.FileMask.WithUnicodeMatching(options));
            return;
        }

        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(options), "cafe\u0301.txt");
        Assert.Equal([1], engine.Find("caf\u00E9*", WordMatchMethod.Exact));
    }

    [Theory]
    [InlineData(UnicodeMatching.CaseFold)]
    [InlineData(UnicodeMatching.GraphemeBoundaries)]
    public void OptionsWithoutNormalization_WorkInEveryGlobalizationMode(UnicodeMatching options)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(options), "Stra\u00DFe", "cafe\u0301");

        Assert.Equal(options == UnicodeMatching.CaseFold ? [1] : [], engine.Find("strasse", WordMatchMethod.Exact));
        Assert.Equal(options == UnicodeMatching.CaseFold ? [2] : [], engine.Find("cafe", WordMatchMethod.Within));
    }

    // The documented probe for globalization-invariant mode: no predefined culture can be created.
    private static bool IsInvariantGlobalizationMode()
    {
        try
        {
            CultureInfo.GetCultureInfo("en-US", predefinedOnly: true);
            return false;
        }
        catch (CultureNotFoundException)
        {
            return true;
        }
    }

    [Theory]
    [InlineData("Stra\u00DFe.txt", "strasse*")]
    [InlineData("STRASSE.txt", "stra\u00DFe*")]
    [InlineData("\u039F\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2.txt", "\u039F\u0394\u03A5\u03A3\u03A3\u0395\u03A5\u03A3*")]
    [InlineData("\u0130stanbul.txt", "istanbul*")]
    [InlineData("\uFB01le.txt", "file*")]
    public void CaseFold_FoldsCharactersThatLowercaseDoesNot(string name, string query)
    {
        var plain = Index(SearchTokenization.FileMask, name);
        var folded = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold), name);

        Assert.Empty(plain.Find(query, WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([1], folded.Find(query, WordMatchMethod.Exact, enableOperators: true));
    }

    [NormalizationTheory]
    [InlineData("\u0141o\u0301dz\u0301 zdje\u0328cia.jpg", "lodz")]
    [InlineData("\u0141\u00F3d\u017A zdj\u0119cia.jpg", "zdjecia")]
    [InlineData("cafe\u0301 cre\u0300me.txt", "creme")]
    [InlineData("caf\u00E9 cr\u00E8me.txt", "cafe")]
    [InlineData("\u00C6r\u00F8 \u00D8resund.txt", "oresund")]
    public void RemoveDiacritics_MatchesUnaccentedQuery_DefaultPreset(string name, string query)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), name);

        Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact));
        Assert.Equal([1], engine.Find(query.ToUpperInvariant(), WordMatchMethod.Within));
    }

    [Fact]
    public void GraphemeBoundaries_WithinDoesNotMatchInsideAGraphemeCluster()
    {
        string[] names = ["cafe\u0301", Family, Man + " solo", "cafe"];
        var plain = Index(SearchTokenization.Default, names);
        var bounded = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries), names);

        Assert.Equal([1, 4], plain.Find("cafe", WordMatchMethod.Within));
        Assert.Equal([4], bounded.Find("cafe", WordMatchMethod.Within));
        Assert.Equal([2, 3], plain.Find(Man, WordMatchMethod.Within));
        Assert.Equal([3], bounded.Find(Man, WordMatchMethod.Within));
        Assert.Empty(bounded.Find(Woman, WordMatchMethod.Within));
    }

    // Lone surrogates (legal in NTFS names) and U+FFFE make string.Normalize throw; folding must not.
    [NormalizationTheory]
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

    [NormalizationFact]
    public void IllFormedText_FoldsTheWellFormedRunsAndKeepsTheRest()
    {
        const UnicodeMatching all = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;

        Assert.Equal("\uD800e\uDC00a\uFFFEss", TextFolder.Fold("\uD800e\u0301\uDC00a\u030A\uFFFE\u00DF", all, isRegex: false));
        Assert.Equal("\uD800\u00E9\uDC00\uFFFE", TextFolder.Fold("\uD800e\u0301\uDC00\uFFFE", UnicodeMatching.Compose, isRegex: false));
    }

    [NormalizationFact]
    public void RegexPattern_IsComposedButNotCaseFolded()
    {
        var engine = Index(
            SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
            "Re\u0301sume\u0301.pdf",
            "Stra\u00DFe.pdf");

        Assert.Equal([1], engine.Find("^re\u0301sume\u0301", WordMatchMethod.Regex));
        Assert.Equal([2], engine.Find(@"^strasse\.pdf$", WordMatchMethod.Regex));
        Assert.Empty(engine.Find("^stra\u00DFe\\.pdf$", WordMatchMethod.Regex));
    }
}
