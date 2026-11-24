using System.Text;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

internal static class QueryMatcher
{
    internal static FastBitSet Match(
        string word,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        if (word.Length == 1 && word[0] == '*')
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

        if (!snapshot.ExactPostings.TryGetValue(word, out var postings))
            return result;

        var docIds = snapshot.PostingDocIds.AsSpan(postings.Offset, postings.Count);
        for (int i = 0; i < docIds.Length; i++)
            result.Add(docIds[i]);

        return result;
    }

    internal static FastBitSet MatchWithin(string word, QueryContext qc, IndexSnapshot snapshot)
    {
        int wordLength = word.Length;
        var result = qc.RentEmptyBitSet();
        var wordSpan = word.AsSpan();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        // Infix matching is single-threaded. Parallel candidate splitting is available in
        // benchmarks/SearchEngine.Sharp.Benchmarks for measurement; it is not part of the library API.

        // For queries of length >= 2 use the bigram index to prune candidates.
        // The bigram maps to word indices already in length-descending order,
        // so the early-exit on length is preserved exactly as in the linear scan.
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        var bigramWordIndices = wordLength >= 2 ? snapshot.BigramWordIndices : null;
        if (bigramWordIndices is { Count: > 0 })
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (!bigramWordIndices.TryGetValue(firstBigram, out var candidates))
                return result;

            foreach (int wordIndex in candidates)
            {
                if (wordLengths[wordIndex] < wordLength)
                    break;

                int searchStart = snapshot.WordEnds[wordIndex] - wordLengths[wordIndex];
                int searchEnd = snapshot.WordEnds[wordIndex];

                MatchSubstring(
                    result,
                    wordSpan,
                    wordsArray,
                    snapshot.PostingDocIds,
                    snapshot.PostingOffsets[wordIndex],
                    snapshot.PostingCounts[wordIndex],
                    searchStart,
                    searchEnd,
                    graphemeBoundaries);
            }

            return result;
        }

        // Fallback: linear scan (handles length-1 queries and empty bigram index).
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            int searchStart = snapshot.WordEnds[wordIndex] - wordLengths[wordIndex];
            int searchEnd = snapshot.WordEnds[wordIndex];

            MatchSubstring(
                result,
                wordSpan,
                wordsArray,
                snapshot.PostingDocIds,
                snapshot.PostingOffsets[wordIndex],
                snapshot.PostingCounts[wordIndex],
                searchStart,
                searchEnd,
                graphemeBoundaries);
        }

        return result;
    }

    internal static FastBitSet MatchGlob(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        var patternSpan = pattern.AsSpan();
        int minLen = GlobMatcher.MinMatchLength(patternSpan);
        bool hasStar = GlobMatcher.PatternHasStar(patternSpan);
        var shape = new GlobMatcher.Shape(patternSpan);
        if (!shape.IsSpecialized)
            return MatchGlobGeneral(patternSpan, minLen, hasStar, qc, snapshot);

        var result = qc.RentEmptyBitSet();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        for (int wordIndex = 0; wordIndex < wordLengths.Length; wordIndex++)
        {
            int wordLength = wordLengths[wordIndex];
            if (wordLength < minLen)
                break;

            if (!hasStar && wordLength != pattern.Length)
                continue;

            int wordStart = snapshot.WordEnds[wordIndex] - wordLength;
            if (!shape.IsMatch(wordsArray.Slice(wordStart, wordLength)))
                continue;

            int postingOffset = snapshot.PostingOffsets[wordIndex];
            int postingCount = snapshot.PostingCounts[wordIndex];
            for (int k = 0; k < postingCount; k++)
                result.Add(snapshot.PostingDocIds[postingOffset + k]);
        }

        return result;
    }

    private static FastBitSet MatchGlobGeneral(ReadOnlySpan<char> patternSpan, int minLen, bool hasStar, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        for (int wordIndex = 0; wordIndex < wordLengths.Length; wordIndex++)
        {
            int wordLength = wordLengths[wordIndex];
            if (wordLength < minLen)
                break;

            if (!hasStar && wordLength != patternSpan.Length)
                continue;

            int wordStart = snapshot.WordEnds[wordIndex] - wordLength;
            if (!GlobMatcher.IsWholeWordMatch(patternSpan, wordsArray.Slice(wordStart, wordLength)))
                continue;

            int postingOffset = snapshot.PostingOffsets[wordIndex];
            int postingCount = snapshot.PostingCounts[wordIndex];
            for (int k = 0; k < postingCount; k++)
                result.Add(snapshot.PostingDocIds[postingOffset + k]);
        }

        return result;
    }

    // Only called on a candidate match when UnicodeMatching.GraphemeBoundaries is set.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsOnGraphemeBoundaries(ReadOnlySpan<char> word, int start, int length)
        => Ascii.IsValid(word) || TextFolder.IsOnGraphemeBoundaries(word, start, length);

    // Finds target inside wordsArray[searchStart..searchEnd) with one vectorized IndexOf (instead of comparing
    // a sliding window at every position). On a match, all documents that contain the indexed word are added to
    // result; one match per word is enough. With UnicodeMatching.GraphemeBoundaries a match must start and end on
    // grapheme cluster boundaries, so later occurrences are tried when the first one splits a cluster.
    private static void MatchSubstring(
        FastBitSet result,
        ReadOnlySpan<char> target,
        ReadOnlySpan<char> wordsArray,
        int[] postingDocIds,
        int postingOffset,
        int postingCount,
        int searchStart,
        int searchEnd,
        bool graphemeBoundaries)
    {
        var word = wordsArray[searchStart..searchEnd];
        int from = 0;
        while (true)
        {
            int index = word[from..].IndexOf(target);
            if (index < 0)
                return;

            index += from;
            if (!graphemeBoundaries || IsOnGraphemeBoundaries(word, index, target.Length))
                break;

            from = index + 1;
        }

        for (int k = 0; k < postingCount; k++)
            result.Add(postingDocIds[postingOffset + k]);
    }
}
