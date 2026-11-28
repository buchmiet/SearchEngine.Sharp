using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.Tests;

// NameTokenizer.TokenizeName turns a name into its distinct lowercase tokens joined by one character; SyntheticPathFeed
// uses it to produce search text. A public utility, so the tests read as a table of inputs and outputs.
public sealed class NameTokenizerTests
{
    [Test]
    public async Task A_name_becomes_its_lowercase_tokens_joined_by_a_hyphen()
    {
        var result = NameTokenizer.TokenizeName("Alpha BETA");

        await Assert.That(result).IsEqualTo("alpha-beta");
    }

    [Test]
    public async Task A_repeated_token_is_kept_once()
    {
        var result = NameTokenizer.TokenizeName("foo bar FOO");

        await Assert.That(result).IsEqualTo("foo-bar");
    }

    [Test]
    public async Task A_null_or_blank_name_gives_an_empty_string()
    {
        await Assert.That(NameTokenizer.TokenizeName(null)).IsEqualTo(string.Empty);
        await Assert.That(NameTokenizer.TokenizeName("   ")).IsEqualTo(string.Empty);
    }
}
