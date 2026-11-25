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
        result.AddAll(snapshot.PostingsOf(word));
        return result;
    }

    // On a match of the word inside an indexed word, all documents that contain the indexed word are added; one match
    // per indexed word is enough.
    internal static FastBitSet MatchWithin(string word, QueryContext qc, IndexSnapshot snapshot)
    {
        int wordLength = word.Length;
        var result = qc.RentEmptyBitSet();
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

                if (ContainsWithin(snapshot.WordAt(wordIndex), word, graphemeBoundaries))
                    result.AddAll(snapshot.PostingsAt(wordIndex));
            }

            return result;
        }

        // Fallback: linear scan (handles length-1 queries and empty bigram index).
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= wordLength; wordIndex++)
        {
            if (ContainsWithin(snapshot.WordAt(wordIndex), word, graphemeBoundaries))
                result.AddAll(snapshot.PostingsAt(wordIndex));
        }

        return result;
    }

    // The pattern is classified once (GlobMatcher.Shape); its general form is matched by GlobMatcher.IsWholeWordMatch.
    internal static FastBitSet MatchGlob(string pattern, QueryContext qc, IndexSnapshot snapshot)
    {
        int minLength = GlobMatcher.MinMatchLength(pattern);
        bool hasStar = pattern.Contains('*');
        bool graphemeBoundaries = snapshot.Tokenization.GraphemeBoundaries;
        var shape = new GlobMatcher.Shape(pattern, graphemeBoundaries);
        if (!shape.IsSpecialized)
            return MatchGlobGeneral(pattern, minLength, hasStar, graphemeBoundaries, qc, snapshot);

        var result = qc.RentEmptyBitSet();
        var wordLengths = snapshot.WordLengths.AsSpan();
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= minLength; wordIndex++)
        {
            if (!hasStar && wordLengths[wordIndex] != pattern.Length)
                continue;

            if (shape.IsMatch(snapshot.WordAt(wordIndex)))
                result.AddAll(snapshot.PostingsAt(wordIndex));
        }

        return result;
    }

    // Kept apart from the specialized scan on purpose: one loop that picks either matcher per word measured about 30%
    // slower for general patterns (BenchmarkDotNet, 100k documents, "p?rt*on").
    private static FastBitSet MatchGlobGeneral(string pattern, int minLength, bool hasStar, bool graphemeBoundaries, QueryContext qc, IndexSnapshot snapshot)
    {
        var result = qc.RentEmptyBitSet();
        var wordLengths = snapshot.WordLengths.AsSpan();
        for (int wordIndex = 0; wordIndex < wordLengths.Length && wordLengths[wordIndex] >= minLength; wordIndex++)
        {
            if (!hasStar && wordLengths[wordIndex] != pattern.Length)
                continue;

            if (GlobMatcher.IsWholeWordMatch(pattern, snapshot.WordAt(wordIndex), graphemeBoundaries))
                result.AddAll(snapshot.PostingsAt(wordIndex));
        }

        return result;
    }

    // Finds target inside word with one vectorized IndexOf (instead of comparing a sliding window at every
    // position). With UnicodeMatching.GraphemeBoundaries a match must start and end on grapheme cluster boundaries
    // (TextFolder.IndexOfOnGraphemeBoundaries). Also used by NameMatcher and by *x* globs (GlobMatcher.Shape).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool ContainsWithin(ReadOnlySpan<char> word, ReadOnlySpan<char> target, bool graphemeBoundaries)
        => (graphemeBoundaries ? TextFolder.IndexOfOnGraphemeBoundaries(word, target) : word.IndexOf(target)) >= 0;
}
