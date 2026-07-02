namespace SearchEngine.Query;

/// <summary>
/// Zero-allocation glob matching for whole indexed tokens.
/// <c>*</c> matches zero or more characters; <c>?</c> matches exactly one character.
/// Patterns are anchored at both ends (full token match).
/// </summary>
internal static class GlobMatcher
{
    internal static bool ContainsMetacharacters(ReadOnlySpan<char> pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is '*' or '?')
                return true;
        }

        return false;
    }

    internal static int MinMatchLength(ReadOnlySpan<char> pattern)
    {
        int count = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '*')
                count++;
        }

        return count;
    }

    internal static bool PatternHasStar(ReadOnlySpan<char> pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="word"/> fully matches <paramref name="pattern"/>.
    /// Consecutive <c>*</c> in the pattern are treated as a single wildcard.
    /// </summary>
    internal static bool IsWholeWordMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word)
    {
        int patternIndex = 0;
        int wordIndex = 0;
        int starPatternIndex = -1;
        int starWordIndex = 0;

        while (wordIndex < word.Length)
        {
            if (patternIndex < pattern.Length)
            {
                char patternChar = pattern[patternIndex];
                if (patternChar == '*')
                {
                    starPatternIndex = patternIndex;
                    starWordIndex = wordIndex;
                    patternIndex++;
                    while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                        patternIndex++;
                    continue;
                }

                if (patternChar == '?' || patternChar == word[wordIndex])
                {
                    patternIndex++;
                    wordIndex++;
                    continue;
                }
            }

            if (starPatternIndex >= 0)
            {
                starWordIndex++;
                wordIndex = starWordIndex;
                patternIndex = starPatternIndex + 1;
                while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                    patternIndex++;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;

        return patternIndex == pattern.Length;
    }
}
