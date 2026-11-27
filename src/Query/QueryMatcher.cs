using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Every matcher scans the words of each segment of the snapshot and adds the postings of the matching words. The
// result can hold dead ordinals; the caller clears them once the whole query is evaluated
// (IndexSnapshot.RemoveDeadOrdinals).
internal static class QueryMatcher
{
    internal static FastBitSet Match(
        string word,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        if (word is ['*'])
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
        foreach (var segment in snapshot.Segments)
            result.AddAll(segment.PostingsOf(word));
        return result;
    }

    // On a match of the word inside an indexed word, all documents that contain the indexed word are added; one match
    // per indexed word is enough.
    internal static FastBitSet MatchWithin(string word, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        foreach (var segment in snapshot.Segments)
            MatchWithin(word, segment, graphemeBoundaries, result);
        return result;
    }

    private static void MatchWithin(string word, IndexSegment segment, bool graphemeBoundaries, FastBitSet result)
    {
        int wordLength = word.Length;
        var wordLengths = segment.WordLengths.AsSpan();

        // Infix matching runs on the calling thread.

        // For queries of length >= 2 use the bigram index to prune candidates.
        // The bigram maps to word indices already in length-descending order,
        // so the early-exit on length is preserved exactly as in the linear scan.
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
                    result.AddAll(segment.PostingsAt(wordIndex));
            }

            return;
        }

        // Fallback: linear scan (handles length-1 queries and empty bigram index).
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            if (ContainsWithin(segment.WordAt(wordIndex), word, graphemeBoundaries))
                result.AddAll(segment.PostingsAt(wordIndex));
        }
    }

    // The pattern is classified once (GlobMatcher.Shape); its general form is matched by GlobMatcher.IsWholeWordMatch.
    internal static FastBitSet MatchGlob(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        int minLength = GlobMatcher.MinMatchLength(pattern);
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        int maxLength = GlobMatcher.MaxMatchLength(pattern, graphemeBoundaries);
        var shape = new GlobMatcher.Shape(pattern, graphemeBoundaries);
        var result = qc.RentEmptyBitSet();
        foreach (var segment in snapshot.Segments)
        {
            if (shape.IsSpecialized)
                MatchGlobSpecialized(shape, minLength, maxLength, segment, result);
            else
                MatchGlobGeneral(pattern, minLength, maxLength, graphemeBoundaries, segment, result);
        }

        return result;
    }

    private static void MatchGlobSpecialized(GlobMatcher.Shape shape, int minLength, int maxLength, IndexSegment segment, FastBitSet result)
    {
        var wordLengths = segment.WordLengths.AsSpan();
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= minLength; wordIndex++)
        {
            if (wordLengths[wordIndex] > maxLength)
                continue;

            if (shape.IsMatch(segment.WordAt(wordIndex)))
                result.AddAll(segment.PostingsAt(wordIndex));
        }
    }

    // Kept apart from the specialized scan on purpose: one loop that picks either matcher per word measured about 30%
    // slower for general patterns (BenchmarkDotNet, 100k documents, "p?rt*on"). Most words are rejected after one
    // character here, so the words are sliced from spans hoisted out of the loop rather than through
    // IndexSegment.WordAt, which measured about 10% slower on this loop.
    private static void MatchGlobGeneral(string pattern, int minLength, int maxLength, bool graphemeBoundaries, IndexSegment segment, FastBitSet result)
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
                result.AddAll(segment.PostingsAt(wordIndex));
        }
    }

    // Finds target inside word with a vectorized IndexOf; the match starts and ends on unit boundaries: code points, or
    // grapheme clusters with UnicodeMatching.GraphemeBoundaries (TextUnitBoundaries.IndexOf). Also used by NameMatcher
    // and by *x* globs (GlobMatcher.Shape).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool ContainsWithin(ReadOnlySpan<char> word, ReadOnlySpan<char> target, bool graphemeBoundaries)
        => TextUnitBoundaries.IndexOf(word, target, graphemeBoundaries) >= 0;
}
