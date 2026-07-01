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
        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (!snapshot.BigramWordIndices.TryGetValue(firstBigram, out var candidates))
                return result;

            foreach (int wordIndex in candidates)
            {
                if (wordLengths[wordIndex] < wordLength)
                    break; // candidates are in length-descending order

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
                    searchEnd);
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
                searchEnd);
        }

        return result;
    }

    // Slides a window of width target.Length across wordsArray[searchStart..searchEnd).
    // On a match, all documents that contain the indexed word are added to result.
    // Only one match is possible per word (a word either contains the substring or not),
    // so the loop breaks immediately after the first match.
    private static void MatchSubstring(
        FastBitSet result,
        ReadOnlySpan<char> target,
        ReadOnlySpan<char> wordsArray,
        int[] postingDocIds,
        int postingOffset,
        int postingCount,
        int searchStart,
        int searchEnd)
    {
        int targetLength = target.Length;

        while (searchStart + targetLength <= searchEnd)
        {
            if (wordsArray.Slice(searchStart, targetLength).SequenceEqual(target))
            {
                for (int k = 0; k < postingCount; k++)
                    result.Add(postingDocIds[postingOffset + k]);
                break;
            }

            searchStart++;
        }
    }
}
