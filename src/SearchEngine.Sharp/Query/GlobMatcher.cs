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

    /// <summary>
    /// Pre-classified glob: the common shapes (<c>abc*</c>, <c>*abc</c>, <c>*abc*</c>, <c>ab*cd</c>,
    /// no <c>?</c>) are matched with span primitives (vectorized StartsWith/EndsWith/IndexOf);
    /// everything else falls back to <see cref="IsWholeWordMatch"/>. Results are identical.
    /// </summary>
    internal readonly ref struct Shape
    {
        private readonly ReadOnlySpan<char> _pattern;
        private readonly ReadOnlySpan<char> _head;
        private readonly ReadOnlySpan<char> _tail;
        private readonly Kind _kind;

        private enum Kind { General, Prefix, Suffix, Contains, PrefixSuffix }

        internal Shape(ReadOnlySpan<char> pattern)
        {
            _pattern = pattern;
            _kind = Kind.General;
            if (pattern.IndexOf('?') >= 0)
                return;

            int first = pattern.IndexOf('*');
            int last = pattern.LastIndexOf('*');
            if (first < 0)
                return;

            bool leading = first == 0;
            bool trailing = last == pattern.Length - 1;
            int innerStart = leading ? 1 : 0;
            int innerEnd = trailing ? pattern.Length - 1 : pattern.Length;
            if (innerEnd <= innerStart)
            {
                _kind = Kind.Contains; // "*" or "**": matches every word
                return;
            }

            var inner = pattern[innerStart..innerEnd];
            if (leading && trailing && inner.IndexOf('*') < 0)
            {
                _kind = Kind.Contains;
                _head = inner;
            }
            else if (leading && !trailing && inner.IndexOf('*') < 0)
            {
                _kind = Kind.Suffix;
                _tail = inner;
            }
            else if (!leading && trailing && inner.IndexOf('*') < 0)
            {
                _kind = Kind.Prefix;
                _head = inner;
            }
            else if (!leading && !trailing && first == last)
            {
                _kind = Kind.PrefixSuffix;
                _head = pattern[..first];
                _tail = pattern[(first + 1)..];
            }
        }

        /// <summary>False when the pattern needs the general matcher (<c>?</c>, several inner <c>*</c>).</summary>
        internal bool IsSpecialized => _kind != Kind.General;

        internal bool IsMatch(ReadOnlySpan<char> word) => _kind switch
        {
            // First-character check before the vectorized compare: most words differ there.
            Kind.Prefix => word.Length >= _head.Length && word[0] == _head[0] && word.StartsWith(_head),
            Kind.Suffix => word.EndsWith(_tail),
            Kind.Contains => word.IndexOf(_head) >= 0,
            Kind.PrefixSuffix => word.Length >= _head.Length + _tail.Length && word.StartsWith(_head) && word.EndsWith(_tail),
            _ => IsWholeWordMatch(_pattern, word),
        };
    }
}
