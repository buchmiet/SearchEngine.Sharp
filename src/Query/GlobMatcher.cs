namespace SearchEngine.Query;

/// <summary>
/// Zero-allocation glob matching for whole indexed tokens.
/// <c>*</c> matches zero or more units of text; <c>?</c> matches exactly one. A unit is a code point (a surrogate pair
/// counts once, a lone surrogate too), or an extended grapheme cluster under
/// <see cref="UnicodeMatching.GraphemeBoundaries"/> (<see cref="TextUnitBoundaries"/>). Every position where a wildcard
/// meets literal text is a unit boundary. Patterns are anchored at both ends (full token match).
/// </summary>
internal static class GlobMatcher
{
    internal static bool ContainsMetacharacters(ReadOnlySpan<char> pattern) => pattern.ContainsAny('*', '?');

    // Every character but '*' consumes at least one UTF-16 unit of the word.
    internal static int MinMatchLength(ReadOnlySpan<char> pattern) => pattern.Length - pattern.Count('*');

    // The longest word in UTF-16 units that the pattern can match: a literal consumes one unit and '?' one code point,
    // at most two units. Unbounded with '*' or when '?' is a grapheme cluster.
    internal static int MaxMatchLength(ReadOnlySpan<char> pattern, bool graphemeBoundaries)
        => graphemeBoundaries || pattern.Contains('*') ? int.MaxValue : pattern.Length + pattern.Count('?');

    /// <summary>
    /// Returns true when <paramref name="word"/> fully matches <paramref name="pattern"/>.
    /// Consecutive <c>*</c> in the pattern are treated as a single wildcard. <paramref name="graphemeBoundaries"/>
    /// makes the units grapheme clusters instead of code points (<see cref="UnicodeMatching.GraphemeBoundaries"/>).
    /// </summary>
    internal static bool IsWholeWordMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, bool graphemeBoundaries = false)
    {
        var units = new TextUnitBoundaries(word, graphemeBoundaries,
            graphemeBoundaries && word.Length < TextUnitBoundaries.MaxStackLength ? stackalloc bool[word.Length + 1] : default);
        try
        {
            return Match(pattern, word, ref units);
        }
        finally
        {
            units.Dispose();
        }
    }

    // A star remembers where it started and gives up one more unit whenever the rest of the pattern fails. A star and
    // a question mark always start on a unit boundary: the word starts on one, a question mark and a star end on one,
    // and a literal before a wildcard must end on one (MayCrossAt).
    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, ref TextUnitBoundaries units)
    {
        int patternIndex = 0;
        int wordIndex = 0;
        int starPatternIndex = -1;
        int starWordIndex = 0;

        while (wordIndex < word.Length)
        {
            if (patternIndex < pattern.Length && MayCrossAt(pattern, patternIndex, wordIndex, ref units))
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

                if (patternChar == '?')
                {
                    patternIndex++;
                    wordIndex = units.Next(wordIndex);
                    continue;
                }

                if (patternChar == word[wordIndex])
                {
                    patternIndex++;
                    wordIndex++;
                    continue;
                }
            }

            if (starPatternIndex >= 0)
            {
                starWordIndex = units.Next(starWordIndex);
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

    // The pattern moves from a literal to a wildcard (or back) at patternIndex: word must have a unit boundary there.
    // Leftmost placement of each literal run stays correct, because the check depends only on where that run is
    // placed.
    private static bool MayCrossAt(ReadOnlySpan<char> pattern, int patternIndex, int wordIndex, ref TextUnitBoundaries units)
        => patternIndex == 0
            || IsWildcard(pattern[patternIndex]) == IsWildcard(pattern[patternIndex - 1])
            || units.IsBoundary(wordIndex);

    private static bool IsWildcard(char ch) => ch is '*' or '?';

    /// <summary>
    /// Pre-classified glob: the common shapes (<c>abc*</c>, <c>*abc</c>, <c>*abc*</c>, <c>ab*cd</c>,
    /// no <c>?</c>) are matched with span primitives (vectorized StartsWith/EndsWith/IndexOf);
    /// everything else falls back to <see cref="IsWholeWordMatch"/>. Results are identical, with and without the
    /// grapheme boundary check.
    /// </summary>
    /// <remarks>
    /// Built once per pattern (per query on an index, once in <see cref="NameMatcher.Create"/>) and stored as offsets
    /// into the pattern string, so it can be kept in a field.
    /// </remarks>
    internal readonly struct Shape
    {
        private readonly string _pattern;
        private readonly int _headStart, _headLength, _tailStart, _tailLength;
        private readonly Kind _kind;
        private readonly bool _graphemeBoundaries;
        private readonly bool _checkBoundaries;

        private enum Kind { General, Prefix, Suffix, Contains, PrefixSuffix }

        private ReadOnlySpan<char> Head => _pattern.AsSpan(_headStart, _headLength);
        private ReadOnlySpan<char> Tail => _pattern.AsSpan(_tailStart, _tailLength);

        internal Shape(string pattern, bool graphemeBoundaries = false)
        {
            _pattern = pattern;
            _graphemeBoundaries = graphemeBoundaries;
            _kind = Kind.General;
            if (pattern.Contains('?'))
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

            bool innerHasStar = pattern.AsSpan(innerStart, innerEnd - innerStart).Contains('*');
            if (leading && trailing && !innerHasStar)
            {
                _kind = Kind.Contains;
                (_headStart, _headLength) = (innerStart, innerEnd - innerStart);
            }
            else if (leading && !trailing && !innerHasStar)
            {
                _kind = Kind.Suffix;
                (_tailStart, _tailLength) = (innerStart, innerEnd - innerStart);
            }
            else if (!leading && trailing && !innerHasStar)
            {
                _kind = Kind.Prefix;
                (_headStart, _headLength) = (innerStart, innerEnd - innerStart);
            }
            else if (!leading && !trailing && first == last)
            {
                _kind = Kind.PrefixSuffix;
                (_headStart, _headLength) = (0, first);
                (_tailStart, _tailLength) = (first + 1, pattern.Length - first - 1);
            }

            // Where a literal meets a *, the word can be split inside a surrogate pair only if the literal ends with a
            // high surrogate there (the head) or starts with a low one (the tail). Any other literal always meets the
            // word on a code point boundary, so for code points the check is settled here, once per pattern.
            _checkBoundaries = graphemeBoundaries
                || (_headLength > 0 && char.IsHighSurrogate(pattern[_headStart + _headLength - 1]))
                || (_tailLength > 0 && char.IsLowSurrogate(pattern[_tailStart]));
        }

        /// <summary>False when the pattern needs the general matcher (<c>?</c>, several inner <c>*</c>).</summary>
        internal bool IsSpecialized => _kind != Kind.General;

        internal bool IsMatch(ReadOnlySpan<char> word) => _kind switch
        {
            // First-character check before the vectorized compare: most words differ there.
            Kind.Prefix => word.Length >= _headLength && word[0] == _pattern[_headStart] && word.StartsWith(Head)
                && (!_checkBoundaries || AreBoundaries(word, _headLength, _headLength)),
            Kind.Suffix => word.EndsWith(Tail)
                && (!_checkBoundaries || AreBoundaries(word, word.Length - _tailLength, word.Length - _tailLength)),
            Kind.Contains => QueryMatcher.ContainsWithin(word, Head, _graphemeBoundaries),
            Kind.PrefixSuffix => word.Length >= _headLength + _tailLength && word.StartsWith(Head) && word.EndsWith(Tail)
                && (!_checkBoundaries || AreBoundaries(word, _headLength, word.Length - _tailLength)),
            _ => IsWholeWordMatch(_pattern, word, _graphemeBoundaries),
        };

        // Both positions are unit boundaries of word. Code points need no boundary table, so that case skips it.
        private bool AreBoundaries(ReadOnlySpan<char> word, int first, int second)
        {
            if (!_graphemeBoundaries)
                return TextUnitBoundaries.IsCodePointBoundary(word, first) && TextUnitBoundaries.IsCodePointBoundary(word, second);

            var boundaries = new TextUnitBoundaries(word, _graphemeBoundaries, default);
            try
            {
                return boundaries.IsBoundary(first) && boundaries.IsBoundary(second);
            }
            finally
            {
                boundaries.Dispose();
            }
        }
    }
}
