using System.Text.RegularExpressions;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Like QueryMatcher: the result can hold dead ordinals; the caller clears them (IndexSnapshot.RemoveDeadOrdinals).
internal static class RegexMatcher
{
    internal static FastBitSet MatchRegex(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();

        if (!RegexPatternCache.TryGet(pattern, (snapshot.Tokenization.UnicodeMatching & UnicodeMatching.CaseFold) != 0, out Regex regex))
            return result;

        try
        {
            foreach (var segment in snapshot.Segments)
            {
                for (int wordIndex = 0; wordIndex < segment.WordCount; wordIndex++)
                {
                    if (regex.IsMatch(segment.WordAt(wordIndex)))
                        result.AddAll(segment.PostingsAt(wordIndex));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // The backtracking fallback can time out; NonBacktracking runs in time linear in the token. A timeout gives
            // an empty result, as an invalid pattern does (RegexPatternCache).
            return qc.RentEmptyBitSet();
        }

        return result;
    }
}
