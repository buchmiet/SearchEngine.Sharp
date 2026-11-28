using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Every matcher adds, segment by segment, the postings of the tokens that match. The result can hold dead ordinals;
// the caller clears them once the text expression is evaluated (IndexSnapshot.RemoveDeadOrdinals).
internal static class QueryMatcher
{
    internal static FastBitSet Match(
        string word,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        if (GlobMatcher.MatchesEveryDocument(word))
            return qc.RentAllTrueBitSet();

        if (GlobMatcher.ContainsMetacharacters(word))
            return MatchGlob(word, qc, snapshot);

        return method == WordMatchMethod.Exact
            ? MatchExact(word, qc, snapshot)
            : MatchWithin(word, qc, snapshot);
    }

    internal static FastBitSet MatchExact(string word, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        for (int s = 0; s < snapshot.Segments.Length; s++)
            result.AddAll(snapshot.Segments[s].PostingsOf(word), snapshot.Offsets[s]);
        return result;
    }

    // When the term occurs inside a token, every document that contains that token is added; one occurrence per token
    // is enough.
    internal static FastBitSet MatchWithin(string word, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        for (int s = 0; s < snapshot.Segments.Length; s++)
            MatchWithin(word, snapshot.Segments[s], snapshot.Offsets[s], graphemeBoundaries, result);
        return result;
    }

    private static void MatchWithin(string word, IndexSegment segment, int offset, bool graphemeBoundaries, FastBitSet result)
    {
        int wordLength = word.Length;
        var wordLengths = segment.WordLengths.AsSpan();

        // A token that contains the term contains its first bigram (IndexSegment.BigramWordIndices). That list keeps
        // the tokens in descending length (IndexSegment, Data layout), so the loop stops at the first shorter token.
        var bigramWordIndices = wordLength >= 2 ? segment.BigramWordIndices : null;
        if (bigramWordIndices is { Count: > 0 })
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (!bigramWordIndices.TryGetValue(firstBigram, out var candidates))
                return;

            foreach (int wordIndex in candidates)
            {
                if (wordLengths[wordIndex] < wordLength)
                    break;

                if (ContainsWithin(segment.WordAt(wordIndex), word, graphemeBoundaries))
                    result.AddAll(segment.PostingsAt(wordIndex), offset);
            }

            return;
        }

        // Linear scan: a one-character term has no bigram, and a segment can have an empty bigram index.
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            if (ContainsWithin(segment.WordAt(wordIndex), word, graphemeBoundaries))
                result.AddAll(segment.PostingsAt(wordIndex), offset);
        }
    }

    // The pattern is classified once (GlobMatcher.Shape); its general form is matched by GlobMatcher.IsWholeWordMatch.
    // Both scans stop at the first token shorter than GlobMatcher.MinMatchLength (IndexSegment, Data layout).
    //
    // A match starts with the literal characters before the first wildcard, compared code unit by code unit whatever the
    // case folding or unit mode, so a segment of PrefixRangeMinWords tokens or more visits only the tokens that start with
    // them (IndexSegment.WordsByText) and the matcher still makes the final decision. useTextOrder false forces the
    // scan (read by tests).
    internal static FastBitSet MatchGlob(string pattern, QueryContext qc, IndexSnapshot snapshot, bool useTextOrder = true)
    {
        var prefix = pattern.AsSpan(0, pattern.AsSpan().IndexOfAny('*', '?'));
        int minLength = GlobMatcher.MinMatchLength(pattern);
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        int maxLength = GlobMatcher.MaxMatchLength(pattern, graphemeBoundaries);
        var shape = new GlobMatcher.Shape(pattern, graphemeBoundaries);
        var result = qc.RentEmptyBitSet();
        for (int s = 0; s < snapshot.Segments.Length; s++)
        {
            var segment = snapshot.Segments[s];
            int offset = snapshot.Offsets[s];
            if (useTextOrder && !prefix.IsEmpty && segment.WordCount >= PrefixRangeMinWords)
            {
                var (start, end) = segment.PrefixRange(prefix);
                if ((long)(end - start) * PrefixRangeMaxShare <= segment.WordCount)
                {
                    MatchGlobRange(shape, minLength, maxLength, segment, start, end, offset, result);
                    continue;
                }
            }

            if (shape.IsSpecialized)
                MatchGlobSpecialized(shape, minLength, maxLength, segment, offset, result);
            else
                MatchGlobGeneral(pattern, minLength, maxLength, graphemeBoundaries, segment, offset, result);
        }

        return result;
    }

    // Smaller segments are scanned: the scan is cheap, and a text order is not worth building for them.
    private const int PrefixRangeMinWords = 256;

    // A run longer than 1 / PrefixRangeMaxShare of the tokens is scanned: the scan reads the tokens in order, and a run
    // that long costs about as much as the scan.
    private const int PrefixRangeMaxShare = 5;

    private static void MatchGlobRange(GlobMatcher.Shape shape, int minLength, int maxLength, IndexSegment segment, int start, int end, int offset, FastBitSet result)
    {
        var order = segment.WordsByText.AsSpan();
        var wordLengths = segment.WordLengths.AsSpan();
        for (int i = start; i < end; i++)
        {
            int wordIndex = order[i];
            int wordLength = wordLengths[wordIndex];
            if (wordLength < minLength || wordLength > maxLength)
                continue;

            if (shape.IsMatch(segment.WordAt(wordIndex)))
                result.AddAll(segment.PostingsAt(wordIndex), offset);
        }
    }

    private static void MatchGlobSpecialized(GlobMatcher.Shape shape, int minLength, int maxLength, IndexSegment segment, int offset, FastBitSet result)
    {
        var wordLengths = segment.WordLengths.AsSpan();
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= minLength; wordIndex++)
        {
            if (wordLengths[wordIndex] > maxLength)
                continue;

            if (shape.IsMatch(segment.WordAt(wordIndex)))
                result.AddAll(segment.PostingsAt(wordIndex), offset);
        }
    }

    // Kept apart from the specialized scan: one loop that picks the matcher per token is slower for general patterns.
    // Most tokens are rejected after one character, so the tokens are sliced from spans hoisted out of the loop;
    // IndexSegment.WordAt costs more per token on this loop.
    private static void MatchGlobGeneral(string pattern, int minLength, int maxLength, bool graphemeBoundaries, IndexSegment segment, int offset, FastBitSet result)
    {
        var wordLengths = segment.WordLengths.AsSpan();
        var wordEnds = segment.WordEnds.AsSpan();
        var words = segment.WordsArray.AsSpan();
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= minLength; wordIndex++)
        {
            int wordLength = wordLengths[wordIndex];
            if (wordLength > maxLength)
                continue;

            if (GlobMatcher.IsWholeWordMatch(pattern, words.Slice(wordEnds[wordIndex] - wordLength, wordLength), graphemeBoundaries))
                result.AddAll(segment.PostingsAt(wordIndex), offset);
        }
    }

    // A substring match that starts and ends on unit boundaries (TextUnitBoundaries.IndexOf).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool ContainsWithin(ReadOnlySpan<char> word, ReadOnlySpan<char> target, bool graphemeBoundaries)
        => TextUnitBoundaries.IndexOf(word, target, graphemeBoundaries) >= 0;
}
