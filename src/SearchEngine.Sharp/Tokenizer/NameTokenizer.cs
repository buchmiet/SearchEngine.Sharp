namespace SearchEngine.Tokenizer;

/// <summary>
/// Reduces a name to its distinct lowercase tokens.
/// </summary>
public static class NameTokenizer
{
    /// <summary>
    /// Splits <paramref name="name"/> at every character that is not a letter or digit, lowercases each token
    /// (invariant culture), drops repeated tokens (the first occurrence stays) and joins the rest with
    /// <paramref name="joinCharacter"/>. Returns an empty string for a null, empty or whitespace name.
    /// </summary>
    public static string TokenizeName(string? name, string joinCharacter = "-")
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var span = name.AsSpan();

        int wordStart = -1;

        for (int i = 0; i < span.Length; i++)
        {
            if (IsTokenChar(span[i]))
            {
                if (wordStart < 0)
                    wordStart = i;
            }
            else if (wordStart >= 0)
            {
                AddToken(span.Slice(wordStart, i - wordStart), tokens, seen);
                wordStart = -1;
            }
        }

        if (wordStart >= 0)
            AddToken(span.Slice(wordStart), tokens, seen);

        return tokens.Count == 0 ? string.Empty : string.Join(joinCharacter, tokens);
    }

    private static bool IsTokenChar(char ch) => char.IsLetterOrDigit(ch);

    private static void AddToken(ReadOnlySpan<char> token, List<string> tokens, HashSet<string> seen)
    {
        if (token.Length == 0)
            return;

        var word = TextNormalizer.CreateNormalizedWord(token);
        if (seen.Add(word))
            tokens.Add(word);
    }
}
