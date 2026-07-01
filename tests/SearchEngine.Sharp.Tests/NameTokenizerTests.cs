using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

public sealed class NameTokenizerTests
{
    [Fact]
    public void TokenizeName_NormalizesCase()
    {
        var result = NameTokenizer.TokenizeName("Alpha BETA");

        Assert.Equal("alpha-beta", result);
    }

    [Fact]
    public void TokenizeName_DeduplicatesTokens()
    {
        var result = NameTokenizer.TokenizeName("foo bar FOO");

        Assert.Equal("foo-bar", result);
    }

    [Fact]
    public void TokenizeName_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NameTokenizer.TokenizeName(null));
        Assert.Equal(string.Empty, NameTokenizer.TokenizeName("   "));
    }
}
