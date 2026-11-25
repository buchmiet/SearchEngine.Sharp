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
    [InlineData("\u017Fun.txt", "sun*")]
    [InlineData("5\u00B5m.txt", "5\u03BCm*")]
    [InlineData("x\u1FBE.txt", "x\u03B9*")]
    [InlineData("\u1C80\u043E\u0434\u0430.txt", "\u0432\u043E\u0434\u0430*")]
    [InlineData("\uD801\uDC00\uD801\uDC01.txt", "\uD801\uDC28\uD801\uDC29*")]
    [InlineData("\uD83A\uDD00.txt", "\uD83A\uDD22*")]
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

    [NormalizationTheory]
    [InlineData("Vi\u1EC7t Nam", "viet")]
    [InlineData("\u0395\u03BB\u03BB\u03AC\u03B4\u03B1", "\u03B5\u03BB\u03BB\u03B1\u03B4\u03B1")]
    [InlineData("\u0419\u043E\u0434", "\u0438\u043E\u0434")]
    [InlineData("\u1F84\u03C1\u03B1", "\u03B1\u03C1\u03B1")]
    public void RemoveDiacritics_StripsLatinGreekAndCyrillicAccents(string name, string query)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), name);

        Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact));
    }

    // Dakuten, Thai tone marks and Indic vowel signs are part of the letter, not accents: stripping them merged
    // different words (バス bus, パス pass, ハス lotus).
    [NormalizationTheory]
    [InlineData("\u30D0\u30B9.txt", "\u30CF\u3099\u30B9.txt", "\u30CF\u30B9.txt")]
    [InlineData("\u30D1\u30B9.txt", "\u30CF\u309A\u30B9.txt", "\u30CF\u30B9.txt")]
    [InlineData("\u304C\u3063\u3053\u3046", "\u304B\u3099\u3063\u3053\u3046", "\u304B\u3063\u3053\u3046")]
    [InlineData("\u0E02\u0E48\u0E32\u0E27", "\u0E02\u0E48\u0E32\u0E27", "\u0E02\u0E32\u0E27")]
    [InlineData("\u0915\u0941\u0932", "\u0915\u0941\u0932", "\u0915\u0932")]
    public void RemoveDiacritics_KeepsMarksOfOtherScripts(string marked, string markedDecomposed, string bare)
    {
        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), marked, bare);

        Assert.Equal([1], engine.Find(marked, WordMatchMethod.Exact));
        Assert.Equal([1], engine.Find(markedDecomposed + "*", WordMatchMethod.Exact));
        Assert.Equal([2], engine.Find(bare, WordMatchMethod.Exact));
    }

    [NormalizationFact]
    public void RemoveDiacritics_StripsOnlyNonSpacingMarksOfTheCombiningDiacriticalMarkBlocks()
    {
        // U+0301 (Combining Diacritical Marks), U+1AB0 (Extended), U+1DC4 (Supplement) and U+FE20 (Half Marks) go;
        // U+1ABE is an enclosing mark, U+20D7 and the keycap U+20E3 are marks for symbols, U+05B8 is a Hebrew point
        // and the supplementary U+1D167 a musical mark: they stay.
        Assert.Equal(
            "abcd\u1ABEe\u20D7f\u20E3\u05D0\u05B8g\uD834\uDD67",
            TextFolder.Fold("a\u0301b\u1AB0c\u1DC4d\uFE20\u1ABEe\u20D7f\u20E3\u05D0\u05B8g\uD834\uDD67", UnicodeMatching.RemoveDiacritics));
    }

    // Where a glob wildcard meets literal text, the name must have a grapheme cluster boundary, as at both ends of a
    // Within match: cafe* does not match a decomposed café, one emoji followed by * does not match a ZWJ family.
    [Fact]
    public void GraphemeBoundaries_GlobWildcardsDoNotSplitAGraphemeCluster()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries);
        string[] names = ["cafe\u0301", "cafe", Family, Man + " solo", "cafe\u0301s"];
        var plain = Index(SearchTokenization.Default, names);
        var engine = Index(tokenization, names);

        Assert.Equal([1, 2, 5], plain.Find("cafe*", WordMatchMethod.Exact));
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe*", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "cafe*", [2], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*afe*", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*\u0301s", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*e\u0301s", [5]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf*\u0301s", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "c*e\u0301s", [5]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe?", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf??", [1]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*a*e*", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "?a*e\u0301*", [1, 5]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, Man + "*", [4]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*" + Woman + "*", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*", [1, 2, 3, 4, 5]);
    }

    [Fact]
    public void GraphemeBoundaries_FileMaskPrefixQueryDoesNotSplitAGraphemeCluster()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries);
        string[] names = ["cafe\u0301.txt", "cafe.txt", "Cafe\u0301 Noir.txt"];
        var engine = Index(tokenization, names);

        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe*", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe\u0301*", [1, 3]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*e.txt", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*\u0301.txt", []);
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

        Assert.Equal("\uD800e\uDC00a\uFFFEss", TextFolder.Fold("\uD800e\u0301\uDC00a\u030A\uFFFE\u00DF", all));
        Assert.Equal("\uD800\u00E9\uDC00\uFFFE", TextFolder.Fold("\uD800e\u0301\uDC00\uFFFE", UnicodeMatching.Compose));
    }

    // A regex pattern runs as written against the folded text: folding the pattern would change its character
    // classes ([e<U+0301>] composed to [é], [b-å] stripped to the invalid range [b-a]).
    [NormalizationFact]
    public void RegexPattern_IsNotFolded_Compose()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold);
        string[] names = ["e.txt", "Re\u0301sume\u0301.pdf", "Stra\u00DFe.pdf"];
        var engine = Index(tokenization, names);

        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[xe\u0301]" + @"\.txt$", [1]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^r\u00E9sum\u00E9", [2]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^re\u0301sume\u0301", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, @"^strasse\.pdf$", [3]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^stra\u00DFe" + @"\.pdf$", []);
    }

    [NormalizationFact]
    public void RegexPattern_IsNotFolded_RemoveDiacritics()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics);
        string[] names = ["report.pdf", "\u00E9cole.txt", "\u00C5l.txt", "bar"];
        var engine = Index(tokenization, names);

        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[b-\u00E5]", [1, 2, 4]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[\u00E0-\u00FF]", []);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^ecole", [2]);
    }

    // Operators are recognized in the query as typed; a word that only folds to and/or/not (nöt, ånd, őr, ør) is
    // a term.
    [NormalizationFact]
    public void RemoveDiacritics_WordsThatFoldToOperatorsStayTerms()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics);
        string[] names = ["n\u00F6t", "\u00E5nd", "\u0151r", "other", "recept", "n\u00F6t recept"];
        var engine = Index(tokenization, names);

        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t", [1, 6], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\u00E5nd", [2], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\u0151r", [3], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "\u00F8r", [3], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "other \u00E5nd", [], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t recept", [6], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t OR \u00E5nd", [1, 2, 6], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "recept NOT n\u00F6t", [5], enableOperators: true);
    }

    // Terms are folded one by one, so a term that folds to a query separator (U+037E GREEK QUESTION MARK is
    // canonically ';') splits into terms the same way the folded index text splits into tokens.
    [NormalizationFact]
    public void Compose_TermThatFoldsToASeparatorSplitsLikeTheIndexText()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.Compose);
        string[] names = ["alpha\u037Ebeta", "alpha gamma"];
        var engine = Index(tokenization, names);

        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "alpha\u037Ebeta", [1]);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "alpha\u037Ebeta", [1], enableOperators: true);
        AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "pha\u037Ebe", [1]);
    }

    private static void AssertFinds(
        SearchEngineSharp engine,
        SearchTokenization tokenization,
        string[] names,
        WordMatchMethod method,
        string query,
        int[] expected,
        bool enableOperators = false)
    {
        Assert.Equal(expected, engine.Find(query, method, enableOperators));
        Assert.Equal(expected.Length, engine.CountMatches(query, method, enableOperators));
        var matcher = NameMatcher.Create(query, method, enableOperators, tokenization);
        Assert.Equal(expected, Enumerable.Range(1, names.Length).Where(id => matcher.IsMatch(names[id - 1])));
    }
}
