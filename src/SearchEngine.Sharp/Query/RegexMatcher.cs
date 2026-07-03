using System.Text.RegularExpressions;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

internal static class RegexMatcher
{
    internal static FastBitSet MatchRegex(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();

        if (pattern.Length == 0 || !RegexPatternCache.TryGet(pattern, out Regex regex))
            return result;

        var wordsArray = snapshot.WordsArray.AsSpan();
        var wordLengths = snapshot.WordLengths.AsSpan();

        for (int wordIndex = 0; wordIndex < wordLengths.Length; wordIndex++)
        {
            int wordLength = wordLengths[wordIndex];
            int wordStart = snapshot.WordEnds[wordIndex] - wordLength;
            if (!regex.IsMatch(wordsArray.Slice(wordStart, wordLength)))
                continue;

            int postingOffset = snapshot.PostingOffsets[wordIndex];
            int postingCount = snapshot.PostingCounts[wordIndex];
            for (int k = 0; k < postingCount; k++)
                result.Add(snapshot.PostingDocIds[postingOffset + k]);
        }

        return result;
    }
}
