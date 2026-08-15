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
        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            if (!TryGetBigramCandidates(word, snapshot, useRarestBigram: false, out var candidates))
                return result;

            ScanBigramCandidates(
                wordSpan, wordLength, candidates, result, wordsArray, wordLengths, snapshot);
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

    /// <summary>Benchmark-only overload comparing first-bigram vs rarest-bigram pruning.</summary>
    internal static FastBitSet MatchWithin(
        string word,
        QueryContext qc,
        IndexSnapshot snapshot,
        bool useRarestBigram)
    {
        int wordLength = word.Length;
        var result = qc.RentEmptyBitSet();
        var wordSpan = word.AsSpan();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            if (!TryGetBigramCandidates(word, snapshot, useRarestBigram, out var candidates))
                return result;

            ScanBigramCandidates(
                wordSpan, wordLength, candidates, result, wordsArray, wordLengths, snapshot);
            return result;
        }

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

    private static bool TryGetBigramCandidates(
        string word,
        IndexSnapshot snapshot,
        bool useRarestBigram,
        out int[] candidates)
    {
        candidates = [];
        if (word.Length < 2)
            return false;

        if (!useRarestBigram || word.Length == 2)
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (snapshot.BigramWordIndices.TryGetValue(firstBigram, out var list))
            {
                candidates = list;
                return true;
            }

            return false;
        }

        int bestKey = -1;
        int bestCount = int.MaxValue;
        for (int j = 0; j + 1 < word.Length; j++)
        {
            int bigram = (word[j] << 16) | word[j + 1];
            if (!snapshot.BigramWordIndices.TryGetValue(bigram, out var list))
                continue;

            if (list.Length < bestCount)
            {
                bestCount = list.Length;
                bestKey = bigram;
            }
        }

        if (bestKey < 0)
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (snapshot.BigramWordIndices.TryGetValue(firstBigram, out var list))
            {
                candidates = list;
                return true;
            }

            return false;
        }

        if (snapshot.BigramWordIndices.TryGetValue(bestKey, out var bestList))
        {
            candidates = bestList;
            return true;
        }

        return false;
    }

    private static void ScanBigramCandidates(
        ReadOnlySpan<char> wordSpan,
        int wordLength,
        int[] candidates,
        FastBitSet result,
        ReadOnlySpan<char> wordsArray,
        ReadOnlySpan<int> wordLengths,
        IndexSnapshot snapshot)
    {
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
                searchEnd);
        }
    }

    internal static FastBitSet MatchGlob(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        var patternSpan = pattern.AsSpan();
        int minLen = GlobMatcher.MinMatchLength(patternSpan);
        bool hasStar = GlobMatcher.PatternHasStar(patternSpan);

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
            if (!GlobMatcher.IsWholeWordMatch(patternSpan, wordsArray.Slice(wordStart, wordLength)))
                continue;

            int postingOffset = snapshot.PostingOffsets[wordIndex];
            int postingCount = snapshot.PostingCounts[wordIndex];
            for (int k = 0; k < postingCount; k++)
                result.Add(snapshot.PostingDocIds[postingOffset + k]);
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
