using SearchEngine.Index;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Benchmarks;

/// <summary>
/// Experimental Within bigram pruning strategies for benchmark comparison only.
/// Production uses <see cref="SearchEngine.Query.QueryMatcher.MatchWithin"/> (first bigram).
/// </summary>
internal static class WithinBigramQueryMatcher
{
    internal static FastBitSet MatchWithinFirstBigram(string word, IndexSnapshot snapshot)
    {
        int wordLength = word.Length;
        var result = new FastBitSet(snapshot.DocumentCount);
        var wordSpan = word.AsSpan();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (!snapshot.BigramWordIndices.TryGetValue(firstBigram, out var candidates))
                return result;

            ScanCandidates(wordSpan, wordLength, candidates, result, wordsArray, wordLengths, snapshot);
            return result;
        }

        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            ScanWord(
                wordSpan,
                wordLength,
                wordIndex,
                result,
                wordsArray,
                wordLengths,
                snapshot);
        }

        return result;
    }

    internal static FastBitSet MatchWithinRarestBigram(string word, IndexSnapshot snapshot)
    {
        int wordLength = word.Length;
        var result = new FastBitSet(snapshot.DocumentCount);
        var wordSpan = word.AsSpan();
        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            if (!TryGetRarestBigramCandidates(word, snapshot, out var candidates))
                return result;

            ScanCandidates(wordSpan, wordLength, candidates, result, wordsArray, wordLengths, snapshot);
            return result;
        }

        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            ScanWord(
                wordSpan,
                wordLength,
                wordIndex,
                result,
                wordsArray,
                wordLengths,
                snapshot);
        }

        return result;
    }

    private static bool TryGetRarestBigramCandidates(
        string word,
        IndexSnapshot snapshot,
        out int[] candidates)
    {
        candidates = [];
        if (word.Length == 2)
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

    private static void ScanCandidates(
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

            ScanWord(wordSpan, wordLength, wordIndex, result, wordsArray, wordLengths, snapshot);
        }
    }

    private static void ScanWord(
        ReadOnlySpan<char> wordSpan,
        int wordLength,
        int wordIndex,
        FastBitSet result,
        ReadOnlySpan<char> wordsArray,
        ReadOnlySpan<int> wordLengths,
        IndexSnapshot snapshot)
    {
        int searchStart = snapshot.WordEnds[wordIndex] - wordLengths[wordIndex];
        int searchEnd = snapshot.WordEnds[wordIndex];

        while (searchStart + wordLength <= searchEnd)
        {
            if (wordsArray.Slice(searchStart, wordLength).SequenceEqual(wordSpan))
            {
                int postingOffset = snapshot.PostingOffsets[wordIndex];
                int postingCount = snapshot.PostingCounts[wordIndex];
                for (int k = 0; k < postingCount; k++)
                    result.Add(snapshot.PostingDocIds[postingOffset + k]);
                break;
            }

            searchStart++;
        }
    }
}
