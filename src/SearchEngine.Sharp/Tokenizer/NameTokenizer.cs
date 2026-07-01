namespace SearchEngine.Tokenizer;

public static class NameTokenizer
{
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
