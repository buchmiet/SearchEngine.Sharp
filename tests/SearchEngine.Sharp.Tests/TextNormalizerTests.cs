using SearchEngine;
using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

public sealed class TextNormalizerTests
{
    [Fact]
    public void GetWordsFromString_NormalizesCaseWithoutDuplicates()
    {
        var words = TextNormalizer.GetWordsFromString("Alpha alpha ALPHA beta");

        Assert.Equal(["alpha", "beta"], words);
    }

    [Fact]
    public void GetWordsFromString_SplitsByConfiguredSeparators()
    {
        var words = TextNormalizer.GetWordsFromString("GA-100.B/C,D|E");

        Assert.Equal(6, words.Count);
        Assert.Equal("100", words[0]);
        Assert.Equal("ga", words[1]);
        Assert.Contains("b", words);
        Assert.Contains("c", words);
        Assert.Contains("d", words);
        Assert.Contains("e", words);
    }

    // Each token is looked up, deduplicated and stored in its lowercase form with ordinal comparison: tokens that
    // differ only in case are indexed once, tokens that are distinct after lowercasing are all indexed.
    // Columns: text, its tokens, queries that must find the document exactly once.
    public static TheoryData<string, string[], string[]> CaseVariants() => new()
    {
        // "Strasse" with sharp s, then in capitals with capital sharp s (U+1E9E), which lowercases to U+00DF:
        // one token "stra\u00DFe".
        { "Stra\u00DFe STRA\u1E9EE.pdf", ["stra\u00DFe", "pdf"], ["stra\u00DFe"] },
        // "k" and the Kelvin sign (U+212A): both lowercase to "k".
        { "k \u212A", ["k"], ["k"] },
        // Final sigma (U+03C2) and medial sigma (U+03C3) stay distinct without UnicodeMatching.CaseFold.
        { "\u03C2 \u03C3", ["\u03C2", "\u03C3"], ["\u03C2", "\u03C3"] },
        // "Odysseus ODYSSEUS.txt" in Greek: the upper-case word lowercases with a medial sigma at the end.
        {
            "\u039F\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2 \u039F\u0394\u03A5\u03A3\u03A3\u0395\u03A5\u03A3.txt",
            ["\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2", "\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C3", "txt"],
            ["\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2", "\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C3"]
        },
        // Micro sign (U+00B5) and Greek mu (U+03BC).
        { "\u00B5 \u03BC", ["\u00B5", "\u03BC"], ["\u00B5", "\u03BC"] },
        // Greek beta (U+03B2) and its symbol variant (U+03D0).
        { "\u03B2 \u03D0", ["\u03B2", "\u03D0"], ["\u03B2", "\u03D0"] },
    };

    [Theory]
    [MemberData(nameof(CaseVariants))]
    public void GetWordsFromString_KeepsEveryLowercaseTokenOnce(string text, string[] expectedWords, string[] queries)
    {
        _ = queries;
        var words = TextNormalizer.GetWordsFromString(text);

        Assert.Equal(expectedWords.Order(StringComparer.Ordinal), words.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CaseVariants))]
    public void Index_PostsTheDocumentOncePerToken(string text, string[] expectedWords, string[] queries)
    {
        _ = expectedWords;
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(new Dictionary<int, string> { [1] = text });
        var engine = new SearchEngineSharp(provider);

        foreach (var query in queries)
        {
            Assert.Equal([1], engine.Find(query, WordMatchMethod.Exact));
            Assert.Equal(1, engine.CountMatches(query, WordMatchMethod.Exact));
            Assert.Equal([1], engine.Find(query, WordMatchMethod.Within, enableOperators: true, SearchSortMode.NaturalSortAscending));
            Assert.True(NameMatcher.Create(query, WordMatchMethod.Exact).IsMatch(text));
        }
    }

    [Fact]
    public void ForEachUniqueWord_RejectsASetThatIgnoresCase()
    {
        int count = 0;
        Assert.Throws<ArgumentException>(() => TextNormalizer.ForEachUniqueWord(
            "a", new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref count, static (ref int c, string _) => c++));
    }
}
