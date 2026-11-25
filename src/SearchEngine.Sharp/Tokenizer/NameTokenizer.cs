namespace SearchEngine.Tokenizer;

/// <summary>
/// Reduces a name to its distinct lowercase tokens, as an optional preprocessing step for
/// <see cref="IndexedEntry.SearchText"/>.
/// </summary>
public static class NameTokenizer
{
    /// <summary>
    /// Splits <paramref name="name"/> at every character that is not a letter or digit (by
    /// <see cref="char.IsLetterOrDigit(char)"/>, so <c>_</c> splits too), lowercases each token (invariant
    /// culture), drops repeated tokens (the first occurrence stays) and joins the rest with
    /// <paramref name="joinCharacter"/>.
    /// </summary>
    /// <param name="name">The name to tokenize.</param>
    /// <param name="joinCharacter">The text placed between tokens.</param>
    /// <returns>The joined tokens, or an empty string for a <see langword="null"/>, empty or whitespace
    /// name.</returns>
    /// <remarks>
    /// Do not apply it to a query: it removes <c>*</c> and <c>?</c>. The test works on UTF-16 code units, so a
    /// character outside the Basic Multilingual Plane (an emoji, or a letter encoded as a surrogate pair) also
    /// splits and is dropped.
    /// </remarks>
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
