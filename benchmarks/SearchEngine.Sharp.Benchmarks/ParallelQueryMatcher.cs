using SearchEngine.Index;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Benchmarks;

/// <summary>
/// Experimental parallel infix matcher used only for benchmark comparison.
/// Not part of the library API.
/// </summary>
internal static class ParallelQueryMatcher
{
    internal static FastBitSet MatchWithinParallel(
        string word,
        IndexSnapshot snapshot,
        int threadCount)
    {
        int wordLength = word.Length;
        var wordLengths = snapshot.WordLengths;

        int[] candidates;
        int cutoff;

        if (wordLength >= 2 && snapshot.BigramWordIndices.Count > 0)
        {
            int firstBigram = (word[0] << 16) | word[1];
            if (!snapshot.BigramWordIndices.TryGetValue(firstBigram, out var bigramCandidates))
                return new FastBitSet(snapshot.DocumentCount);

            candidates = bigramCandidates;
            cutoff = FindCutoff(candidates, wordLength, wordLengths);
        }
        else
        {
            cutoff = FindLinearCutoff(wordLengths, wordLength);
            candidates = BuildSequentialIndices(cutoff);
        }

        if (cutoff == 0)
            return new FastBitSet(snapshot.DocumentCount);

        int actualThreads = Math.Min(threadCount, cutoff);
        var partials = new FastBitSet[actualThreads];

        Parallel.For(0, actualThreads, t =>
        {
            int start = t * cutoff / actualThreads;
            int end = (t + 1) * cutoff / actualThreads;
            var local = new FastBitSet(snapshot.DocumentCount);
            var localWord = word.AsSpan();
            var localWordsArray = snapshot.WordsArray.AsSpan();

            for (int ci = start; ci < end; ci++)
            {
                int wordIndex = candidates[ci];
                int wLen = wordLengths[wordIndex];
                int searchStart = snapshot.WordEnds[wordIndex] - wLen;
                int searchEnd = snapshot.WordEnds[wordIndex];

                while (searchStart + wordLength <= searchEnd)
                {
                    if (localWordsArray.Slice(searchStart, wordLength).SequenceEqual(localWord))
                    {
                        int po = snapshot.PostingOffsets[wordIndex];
                        int pc = snapshot.PostingCounts[wordIndex];
                        for (int k = 0; k < pc; k++)
                            local.Add(snapshot.PostingDocIds[po + k]);
                        break;
                    }
                    searchStart++;
                }
            }

            partials[t] = local;
        });

        var result = partials[0];
        for (int t = 1; t < actualThreads; t++)
            result.UnionWith(partials[t]);

        return result;
    }

    private static int FindCutoff(int[] candidates, int wordLength, int[] wordLengths)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (wordLengths[candidates[i]] < wordLength)
                return i;
        }
        return candidates.Length;
    }

    private static int FindLinearCutoff(int[] wordLengths, int wordLength)
    {
        for (int i = 0; i < wordLengths.Length; i++)
        {
            if (wordLengths[i] < wordLength)
                return i;
        }
        return wordLengths.Length;
    }

    private static int[] BuildSequentialIndices(int count)
    {
        var arr = new int[count];
        for (int i = 0; i < count; i++)
            arr[i] = i;
        return arr;
    }
}
