using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

public sealed class NameTokenizerTests
{
    [Test]
    public async Task TokenizeName_NormalizesCase()
    {
        var result = NameTokenizer.TokenizeName("Alpha BETA");

        await Assert.That(result).IsEqualTo("alpha-beta");
    }

    [Test]
    public async Task TokenizeName_DeduplicatesTokens()
    {
        var result = NameTokenizer.TokenizeName("foo bar FOO");

        await Assert.That(result).IsEqualTo("foo-bar");
    }

    [Test]
    public async Task TokenizeName_EmptyInput_ReturnsEmpty()
    {
        await Assert.That(NameTokenizer.TokenizeName(null)).IsEqualTo(string.Empty);
        await Assert.That(NameTokenizer.TokenizeName("   ")).IsEqualTo(string.Empty);
    }
}
