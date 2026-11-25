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

    [Test]
    public async Task Default_DecomposedNameIsNotFoundByComposedQuery()
    {
        var engine = Index(SearchTokenization.FileMask, ZazolcNfd + ".txt");

        await Assert.That(engine.Find(ZazolcNfc.ToLowerInvariant() + "*", WordMatchMethod.Exact, enableOperators: true)).IsEmpty();
    }

    [Test]
    [SkipUnlessNormalization]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Compose_MatchesAcrossNfcAndNfd(bool nameDecomposed)
    {
        string name = (nameDecomposed ? ZazolcNfd : ZazolcNfc) + ".txt";
        string query = (nameDecomposed ? ZazolcNfc : ZazolcNfd) + "*";
        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose), name, "other.txt");

        await Assert.That(engine.Find(query, WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find(ZazolcNfd + ".txt", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    // Compose and RemoveDiacritics need Unicode normalization, which globalization-invariant mode
    // (InvariantGlobalization, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1) turns into a no-op. Asking for them there
    // fails loudly instead of matching nothing. Run the suite once with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 to
    // cover the second branch.
    [Test]
    [Arguments(UnicodeMatching.Compose)]
    [Arguments(UnicodeMatching.RemoveDiacritics)]
    [Arguments(UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries)]
    public async Task NormalizingOptions_WorkWithICU_AndThrowInInvariantGlobalizationMode(UnicodeMatching options)
    {
        if (IsInvariantGlobalizationMode())
        {
            await Assert.That(() => SearchTokenization.FileMask.WithUnicodeMatching(options)).ThrowsExactly<PlatformNotSupportedException>();
            return;
        }

        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(options), "cafe\u0301.txt");
        await Assert.That(engine.Find("caf\u00E9*", WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(UnicodeMatching.CaseFold)]
    [Arguments(UnicodeMatching.GraphemeBoundaries)]
    public async Task OptionsWithoutNormalization_WorkInEveryGlobalizationMode(UnicodeMatching options)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(options), "Stra\u00DFe", "cafe\u0301");
        int[] strasse = options == UnicodeMatching.CaseFold ? [1] : [];
        int[] cafe = options == UnicodeMatching.CaseFold ? [2] : [];

        await Assert.That(engine.Find("strasse", WordMatchMethod.Exact)).IsEquivalentTo(strasse, CollectionOrdering.Matching);
        await Assert.That(engine.Find("cafe", WordMatchMethod.Within)).IsEquivalentTo(cafe, CollectionOrdering.Matching);
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

    [Test]
    [Arguments("Stra\u00DFe.txt", "strasse*")]
    [Arguments("STRASSE.txt", "stra\u00DFe*")]
    [Arguments("\u039F\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2.txt", "\u039F\u0394\u03A5\u03A3\u03A3\u0395\u03A5\u03A3*")]
    [Arguments("\u0130stanbul.txt", "istanbul*")]
    [Arguments("\uFB01le.txt", "file*")]
    [Arguments("\u017Fun.txt", "sun*")]
    [Arguments("5\u00B5m.txt", "5\u03BCm*")]
    [Arguments("x\u1FBE.txt", "x\u03B9*")]
    [Arguments("\u1C80\u043E\u0434\u0430.txt", "\u0432\u043E\u0434\u0430*")]
    [Arguments("\uD801\uDC00\uD801\uDC01.txt", "\uD801\uDC28\uD801\uDC29*")]
    [Arguments("\uD83A\uDD00.txt", "\uD83A\uDD22*")]
    public async Task CaseFold_FoldsCharactersThatLowercaseDoesNot(string name, string query)
    {
        var plain = Index(SearchTokenization.FileMask, name);
        var folded = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold), name);

        await Assert.That(plain.Find(query, WordMatchMethod.Exact, enableOperators: true)).IsEmpty();
        await Assert.That(folded.Find(query, WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    [SkipUnlessNormalization]
    [Arguments("\u0141o\u0301dz\u0301 zdje\u0328cia.jpg", "lodz")]
    [Arguments("\u0141\u00F3d\u017A zdj\u0119cia.jpg", "zdjecia")]
    [Arguments("cafe\u0301 cre\u0300me.txt", "creme")]
    [Arguments("caf\u00E9 cr\u00E8me.txt", "cafe")]
    [Arguments("\u00C6r\u00F8 \u00D8resund.txt", "oresund")]
    public async Task RemoveDiacritics_MatchesUnaccentedQuery_DefaultPreset(string name, string query)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), name);

        await Assert.That(engine.Find(query, WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find(query.ToUpperInvariant(), WordMatchMethod.Within)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    [SkipUnlessNormalization]
    [Arguments("Vi\u1EC7t Nam", "viet")]
    [Arguments("\u0395\u03BB\u03BB\u03AC\u03B4\u03B1", "\u03B5\u03BB\u03BB\u03B1\u03B4\u03B1")]
    [Arguments("\u0419\u043E\u0434", "\u0438\u043E\u0434")]
    [Arguments("\u1F84\u03C1\u03B1", "\u03B1\u03C1\u03B1")]
    public async Task RemoveDiacritics_StripsLatinGreekAndCyrillicAccents(string name, string query)
    {
        var engine = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), name);

        await Assert.That(engine.Find(query, WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    // Dakuten, Thai tone marks and Indic vowel signs are part of the letter, not accents: stripping them merged
    // different words (バス bus, パス pass, ハス lotus).
    [Test]
    [SkipUnlessNormalization]
    [Arguments("\u30D0\u30B9.txt", "\u30CF\u3099\u30B9.txt", "\u30CF\u30B9.txt")]
    [Arguments("\u30D1\u30B9.txt", "\u30CF\u309A\u30B9.txt", "\u30CF\u30B9.txt")]
    [Arguments("\u304C\u3063\u3053\u3046", "\u304B\u3099\u3063\u3053\u3046", "\u304B\u3063\u3053\u3046")]
    [Arguments("\u0E02\u0E48\u0E32\u0E27", "\u0E02\u0E48\u0E32\u0E27", "\u0E02\u0E32\u0E27")]
    [Arguments("\u0915\u0941\u0932", "\u0915\u0941\u0932", "\u0915\u0932")]
    public async Task RemoveDiacritics_KeepsMarksOfOtherScripts(string marked, string markedDecomposed, string bare)
    {
        var engine = Index(SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics), marked, bare);

        await Assert.That(engine.Find(marked, WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find(markedDecomposed + "*", WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find(bare, WordMatchMethod.Exact)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    [SkipUnlessNormalization]
    public async Task RemoveDiacritics_StripsOnlyNonSpacingMarksOfTheCombiningDiacriticalMarkBlocks()
    {
        // U+0301 (Combining Diacritical Marks), U+1AB0 (Extended), U+1DC4 (Supplement) and U+FE20 (Half Marks) go;
        // U+1ABE is an enclosing mark, U+20D7 and the keycap U+20E3 are marks for symbols, U+05B8 is a Hebrew point
        // and the supplementary U+1D167 a musical mark: they stay.
        await Assert.That(TextFolder.Fold("a\u0301b\u1AB0c\u1DC4d\uFE20\u1ABEe\u20D7f\u20E3\u05D0\u05B8g\uD834\uDD67", UnicodeMatching.RemoveDiacritics))
            .IsEqualTo("abcd\u1ABEe\u20D7f\u20E3\u05D0\u05B8g\uD834\uDD67");
    }

    // Where a glob wildcard meets literal text, the name must have a grapheme cluster boundary, as at both ends of a
    // Within match: cafe* does not match a decomposed café, one emoji followed by * does not match a ZWJ family.
    [Test]
    public async Task GraphemeBoundaries_GlobWildcardsDoNotSplitAGraphemeCluster()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries);
        string[] names = ["cafe\u0301", "cafe", Family, Man + " solo", "cafe\u0301s"];
        var plain = Index(SearchTokenization.Default, names);
        var engine = Index(tokenization, names);

        await Assert.That(plain.Find("cafe*", WordMatchMethod.Exact)).IsEquivalentTo([1, 2, 5], CollectionOrdering.Matching);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe*", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "cafe*", [2], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*afe*", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*\u0301s", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*e\u0301s", [5]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf*\u0301s", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "c*e\u0301s", [5]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe?", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf?", [1, 2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf??", [5]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*a*e*", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "?a*e\u0301*", [1, 5]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, Man + "*", [4]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*" + Woman + "*", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*", [1, 2, 3, 4, 5]);
    }

    [Test]
    public async Task GraphemeBoundaries_FileMaskPrefixQueryDoesNotSplitAGraphemeCluster()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries);
        string[] names = ["cafe\u0301.txt", "cafe.txt", "Cafe\u0301 Noir.txt"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe*", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "cafe\u0301*", [1, 3]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*e.txt", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*\u0301.txt", []);
    }

    [Test]
    public async Task GraphemeBoundaries_WithinDoesNotMatchInsideAGraphemeCluster()
    {
        string[] names = ["cafe\u0301", Family, Man + " solo", "cafe"];
        var plain = Index(SearchTokenization.Default, names);
        var bounded = Index(SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries), names);

        await Assert.That(plain.Find("cafe", WordMatchMethod.Within)).IsEquivalentTo([1, 4], CollectionOrdering.Matching);
        await Assert.That(bounded.Find("cafe", WordMatchMethod.Within)).IsEquivalentTo([4], CollectionOrdering.Matching);
        await Assert.That(plain.Find(Man, WordMatchMethod.Within)).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
        await Assert.That(bounded.Find(Man, WordMatchMethod.Within)).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(bounded.Find(Woman, WordMatchMethod.Within)).IsEmpty();
    }

    // Lone surrogates (legal in NTFS names) and U+FFFE make string.Normalize throw; folding must not.
    [Test]
    [SkipUnlessNormalization]
    [Arguments(UnicodeMatching.Compose)]
    [Arguments(UnicodeMatching.RemoveDiacritics)]
    [Arguments(UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries)]
    public async Task IllFormedText_IsIndexedAndMatchesItself(UnicodeMatching options)
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(options);
        string[] names = ["ok.txt", "report\uD800.txt", "\uDC00cafe\u0301.txt", "a\uFFFEb.txt", "zdj\uD83D\uDE00.jpg"];
        var engine = Index(tokenization, names);

        await Assert.That(engine.Find("ok.txt", WordMatchMethod.Exact)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find("report\uD800.txt", WordMatchMethod.Exact)).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(engine.Find("report\uD800*", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(engine.Find("\uDC00caf\u00E9*", WordMatchMethod.Exact)).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(engine.Find("a\uFFFEb.txt", WordMatchMethod.Exact)).IsEquivalentTo([4], CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("*\uFFFE*", WordMatchMethod.Exact)).IsEqualTo(1);
        await Assert.That(engine.Find("zdj\uD83D", WordMatchMethod.Exact)).IsEmpty();
        await Assert.That(engine.Find("zdj\uD83D\uDE00*", WordMatchMethod.Exact)).IsEquivalentTo([5], CollectionOrdering.Matching);

        foreach (string name in names)
            await Assert.That(NameMatcher.Create(name, WordMatchMethod.Exact, tokenization: tokenization).IsMatch(name)).IsTrue();
        await Assert.That(NameMatcher.Create("zdj\uD83D", WordMatchMethod.Exact, tokenization: tokenization).IsMatch(names[4])).IsFalse();
    }

    [Test]
    [SkipUnlessNormalization]
    public async Task IllFormedText_FoldsTheWellFormedRunsAndKeepsTheRest()
    {
        const UnicodeMatching all = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;

        await Assert.That(TextFolder.Fold("\uD800e\u0301\uDC00a\u030A\uFFFE\u00DF", all)).IsEqualTo("\uD800e\uDC00a\uFFFEss");
        await Assert.That(TextFolder.Fold("\uD800e\u0301\uDC00\uFFFE", UnicodeMatching.Compose)).IsEqualTo("\uD800\u00E9\uDC00\uFFFE");
    }

    // Windows NLS normalization (DOTNET_SYSTEM_GLOBALIZATION_USENLS=1) also rejects the noncharacters U+FDD0 to
    // U+FDEF and U+nFFFE / U+nFFFF of every plane, which ICU accepts. Folding gives the same result on both: a
    // noncharacter never composes, so it is kept as it is and the text around it is folded.
    [Test]
    [SkipUnlessNormalization]
    [Arguments(UnicodeMatching.Compose | UnicodeMatching.CaseFold)]
    [Arguments(UnicodeMatching.RemoveDiacritics)]
    public async Task Noncharacters_AreKeptAndTheTextAroundThemIsFolded(UnicodeMatching options)
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(options);
        string[] names = ["Cafe\u0301\uFFFF.txt", "x\uFDD0e\u0301", "\uFDEF\uD83F\uDFFFe\u0301\uDBFF\uDFFE", "other"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "caf\u00E9\uFFFF.txt", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "x\uFDD0\u00E9", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\uFDEF\uD83F\uDFFF\u00E9\uDBFF\uDFFE", [3]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "\uFFFF", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "*\uFDD0*", [2], enableOperators: true);
        foreach (string name in names)
            await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, name, [Array.IndexOf(names, name) + 1]);

        string expected = (options & UnicodeMatching.RemoveDiacritics) != 0 ? "e\uFFFFe\uFDD0e" : "\u00E9\uFFFF\u00E9\uFDD0\u00E9";
        await Assert.That(TextFolder.Fold("e\u0301\uFFFFe\u0301\uFDD0e\u0301", options)).IsEqualTo(expected);
    }

    // A regex pattern runs as written against the folded text: folding the pattern would change its character
    // classes ([e<U+0301>] composed to [é], [b-å] stripped to the invalid range [b-a]).
    [Test]
    [SkipUnlessNormalization]
    public async Task RegexPattern_IsNotFolded_Compose()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold);
        string[] names = ["e.txt", "Re\u0301sume\u0301.pdf", "Stra\u00DFe.pdf"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[xe\u0301]" + @"\.txt$", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^r\u00E9sum\u00E9", [2]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^re\u0301sume\u0301", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, @"^strasse\.pdf$", [3]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^stra\u00DFe" + @"\.pdf$", []);
    }

    [Test]
    [SkipUnlessNormalization]
    public async Task RegexPattern_IsNotFolded_RemoveDiacritics()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics);
        string[] names = ["report.pdf", "\u00E9cole.txt", "\u00C5l.txt", "bar"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[b-\u00E5]", [1, 2, 4]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^[\u00E0-\u00FF]", []);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Regex, "^ecole", [2]);
    }

    // Operators are recognized in the query as typed; a word that only folds to and/or/not (nöt, ånd, őr, ør) is
    // a term.
    [Test]
    [SkipUnlessNormalization]
    public async Task RemoveDiacritics_WordsThatFoldToOperatorsStayTerms()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics);
        string[] names = ["n\u00F6t", "\u00E5nd", "\u0151r", "other", "recept", "n\u00F6t recept"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t", [1, 6], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\u00E5nd", [2], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\u0151r", [3], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "\u00F8r", [3], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "other \u00E5nd", [], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t recept", [6], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "n\u00F6t OR \u00E5nd", [1, 2, 6], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "recept NOT n\u00F6t", [5], enableOperators: true);
    }

    // Terms are folded one by one, so a term that folds to a query separator (U+037E GREEK QUESTION MARK is
    // canonically ';') splits into terms the same way the folded index text splits into tokens.
    [Test]
    [SkipUnlessNormalization]
    public async Task Compose_TermThatFoldsToASeparatorSplitsLikeTheIndexText()
    {
        var tokenization = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.Compose);
        string[] names = ["alpha\u037Ebeta", "alpha gamma"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "alpha\u037Ebeta", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "alpha\u037Ebeta", [1], enableOperators: true);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Within, "pha\u037Ebe", [1]);
    }

    // Lowercasing after composition can leave text that is not NFC: T + U+0308 has no uppercase precomposed form, but
    // t + U+0308 composes to U+1E97. The name, its lowercase form and its NFC form must all find each other.
    [Test]
    [SkipUnlessNormalization]
    [Arguments("SALAT\u0308", "salat\u0308")]
    [Arguments("SALAT\u0308", "sala\u1E97")]
    [Arguments("J\u030CAN", "j\u030Can")]
    [Arguments("J\u030CAN", "\u01F0an")]
    [Arguments("sala\u1E97", "SALAT\u0308")]
    public async Task Compose_NameMatchesItsLowercaseAndComposedForms(string name, string query)
    {
        foreach (var options in new[] { UnicodeMatching.Compose, UnicodeMatching.Compose | UnicodeMatching.CaseFold })
        {
            var tokenization = SearchTokenization.Default.WithUnicodeMatching(options);
            string[] names = [name, "other"];
            var engine = Index(tokenization, names);

            await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, query, [1]);
            await AssertFinds(engine, tokenization, names, WordMatchMethod.Within, query, [1]);
        }
    }

    // Text and queries are split at the separators as written and then folded, so a combining mark after a
    // separator never composes with it: x, '=' and U+0338 stay the token x and the token U+0338 y instead of one
    // token with U+2260 NOT EQUAL TO, on both sides.
    [Test]
    [SkipUnlessNormalization]
    public async Task Compose_ASeparatorFollowedByACombiningMarkSplitsTextAndQueryAlike()
    {
        var tokenization = SearchTokenization.Create(" =", " =").WithUnicodeMatching(UnicodeMatching.Compose);
        string[] names = ["x=\u0338y", "x\u2260y", "other"];
        var engine = Index(tokenization, names);

        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "x=\u0338y", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "\u0338y", [1]);
        await AssertFinds(engine, tokenization, names, WordMatchMethod.Exact, "x\u2260y", [2]);
    }

    // Generated names of letters in both cases, combining marks, characters that fold or compose to separators,
    // noncharacters and lone surrogates: every name is found by itself and by its per-character lowercase form, on
    // the index and by NameMatcher. I + U+0307 is left out: under Compose it is U+0130, which folds to a plain i, so
    // its per-character lowercase i + U+0307 is another word (docs/query-semantics.md, Unicode matching).
    [Test]
    [SkipUnlessNormalization]
    public async Task GeneratedNames_AreFoundByThemselvesAndTheirLowercaseForm()
    {
        string[] pool =
        [
            "a", "T", "t", "J", "I", "i", "S", "\u00DF", "\u03A3", "\u03C2", "\u0130", "\u212A", " ", "-", ".", "=",
            "\u0308", "\u030C", "\u0301", "\u0323", "\u0338", "\u0345", "\u037E", "\uFFFF", "\uFDD0", "\uD800",
            "\uD801\uDC00", "\u1E97", "\u01F0", "e\u0301",
        ];
        var random = new Random(1234);
        var names = new string[300];
        for (int i = 0; i < names.Length; i++)
            names[i] = string.Concat(Enumerable.Range(0, random.Next(1, 7)).Select(_ => pool[random.Next(pool.Length)]));

        var failures = new List<string>();
        foreach (var options in new[]
                 {
                     UnicodeMatching.Compose,
                     UnicodeMatching.Compose | UnicodeMatching.CaseFold,
                     UnicodeMatching.RemoveDiacritics | UnicodeMatching.CaseFold,
                 })
        {
            foreach (var preset in new[] { SearchTokenization.Default, SearchTokenization.FileMask })
            {
                var tokenization = preset.WithUnicodeMatching(options);
                var engine = Index(tokenization, names);
                for (int i = 0; i < names.Length; i++)
                {
                    // Skipped: a name without tokens after folding (U+037E folds to the separator ';'), and under FileMask
                    // a name with a space, which a query cannot hold in one term.
                    if (tokenization.FoldIndexText(names[i]).AsSpan().IndexOfAnyExcept(tokenization.IndexSeparatorValues) < 0
                        || (preset == SearchTokenization.FileMask && names[i].Contains(' ')))
                        continue;

                    string lower = string.Concat(names[i].Select(char.ToLowerInvariant));
                    foreach (string query in new[] { names[i], lower })
                    {
                        bool index = engine.Find(query, WordMatchMethod.Exact).Contains(i + 1);
                        bool matcher = NameMatcher.Create(query, WordMatchMethod.Exact, tokenization: tokenization).IsMatch(names[i]);
                        if (!index || !matcher)
                            failures.Add($"{options} {preset.IndexSeparators.Length}: {Escape(names[i])} by {Escape(query)}: index {index}, matcher {matcher}");
                    }
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures.Take(20)));

        static string Escape(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:X4}"));
    }

    private static async Task AssertFinds(
        SearchEngineSharp engine,
        SearchTokenization tokenization,
        string[] names,
        WordMatchMethod method,
        string query,
        int[] expected,
        bool enableOperators = false)
    {
        await Assert.That(engine.Find(query, method, enableOperators)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches(query, method, enableOperators)).IsEqualTo(expected.Length);
        var matcher = NameMatcher.Create(query, method, enableOperators, tokenization);
        await Assert.That(Enumerable.Range(1, names.Length).Where(id => matcher.IsMatch(names[id - 1]))).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
