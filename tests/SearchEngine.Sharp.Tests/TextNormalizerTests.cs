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
}
