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

        try
        {
            for (int wordIndex = 0; wordIndex < snapshot.WordLengths.Length; wordIndex++)
            {
                if (regex.IsMatch(snapshot.WordAt(wordIndex)))
                    result.AddAll(snapshot.PostingsAt(wordIndex));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Only reachable on the backtracking fallback path (NonBacktracking
            // cannot time out in practice). Treat like an invalid pattern: empty result.
            return qc.RentEmptyBitSet();
        }

        return result;
    }
}
