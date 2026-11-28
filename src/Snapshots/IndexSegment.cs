using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// One immutable part of an IndexSnapshot: the inverted index, sort texts and facet columns of a set of documents,
// built by IndexSnapshotBuilder.
//
// ── Data layout ──
// The distinct tokens are packed one after another into one char[] (WordsArray). Arrays indexed by token index i
// describe the slice of each token:
//
//   WordLengths[i] : length of token i in characters
//   WordEnds[i]    : exclusive end of token i in WordsArray, so token i is
//                    WordsArray[WordEnds[i] - WordLengths[i] .. WordEnds[i])
//
// Tokens are stored in descending order of length, so the Within and glob scans (QueryMatcher) stop at the first token
// shorter than the query or than the shortest match of the pattern.
//
// The posting lists (the documents that contain each token) are packed into PostingDocIds. Arrays indexed by token
// index i describe the slice of each list:
//
//   PostingOffsets[i] : start of the postings of token i in PostingDocIds
//   PostingCounts[i]  : number of documents that contain token i
//
// PostingDocIds holds snapshot ordinals, not record ids, ascending within each token. The documents of a segment have
// the ordinals FirstOrdinal .. FirstOrdinal + DocumentCount - 1; the per-document arrays (RecordIds, SortTexts,
// FacetColumns) are indexed by ordinal - FirstOrdinal.
//
// ── Thread safety ──
// A segment is immutable after construction except for two derived structures built on first use: the bigram index
// and the sort permutation. Each is built at most once per segment: the first caller builds it under a per-structure
// lock while concurrent first callers wait on that lock. A build that throws publishes nothing, so the next caller
// builds again. Nothing is built at publish time. The base segment of an index outlives many snapshots
// (IndexSnapshot), so its structures survive the publishes of small changes.
internal sealed class IndexSegment
{
    internal static readonly IndexSegment Empty = new(
        firstOrdinal: 0,
        recordIds: [],
        exactPostings: [],
        wordsArray: [],
        postingDocIds: [],
        postingOffsets: [],
        wordEnds: [],
        wordLengths: [],
        postingCounts: [],
        sortTexts: [],
        facetColumns: []);

    // The snapshot ordinal of the first document.
    internal readonly int FirstOrdinal;

    // The record id of each document.
    internal readonly int[] RecordIds;

    // Token → (offset, count) in PostingDocIds, for lookups by text (PostingsOf): the token arrays are sorted by
    // length, so they cannot be searched by text.
    internal readonly Dictionary<string, WordPostings> ExactPostings;

    // The token and posting arrays (Data layout above).
    internal readonly char[] WordsArray;
    internal readonly int[] PostingDocIds;
    internal readonly int[] PostingOffsets;
    internal readonly int[] WordEnds;
    internal readonly int[] WordLengths;
    internal readonly int[] PostingCounts;

    // The sort text (IndexedEntry.SortText) of each document.
    internal readonly string[] SortTexts;

    // Facet name → the value of each document. A facet that no document of the segment carries has no column.
    internal readonly Dictionary<string, long[]> FacetColumns;

    internal int DocumentCount => RecordIds.Length;

    internal int WordCount => WordLengths.Length;

    // Token i, read from WordsArray.
    internal ReadOnlySpan<char> WordAt(int wordIndex)
        => WordsArray.AsSpan(WordEnds[wordIndex] - WordLengths[wordIndex], WordLengths[wordIndex]);

    // Ordinals of the documents that contain token i.
    internal ReadOnlySpan<int> PostingsAt(int wordIndex)
        => PostingDocIds.AsSpan(PostingOffsets[wordIndex], PostingCounts[wordIndex]);

    // Ordinals of the documents that contain exactly this token; empty when the token is not indexed.
    internal ReadOnlySpan<int> PostingsOf(string word)
        => ExactPostings.TryGetValue(word, out var postings) ? PostingDocIds.AsSpan(postings.Offset, postings.Count) : default;

    // Bigram index: every bigram of the indexed tokens, packed as (c0 << 16 | c1) → the indices of the tokens that
    // contain it, ascending and so length-descending (Data layout above). Only Within queries read it: MatchWithin
    // reads the list of the first bigram of the query, since a token that contains the query contains that bigram.
    private Dictionary<int, int[]>? _bigramWordIndices;
    private object? _bigramBuildLock;
    private int _bigramBuildCount;

    internal Dictionary<int, int[]> BigramWordIndices
        => Volatile.Read(ref _bigramWordIndices)
           ?? LazyInitializer.EnsureInitialized(ref _bigramWordIndices, ref _bigramBuildLock, BuildBigramWordIndices);

    // Number of times the bigram index was built on this segment (read by tests).
    internal int BigramBuildCount => Volatile.Read(ref _bigramBuildCount);

    // Sort permutation for NaturalSortAscending (GetSortedPermutation).
    private int[]? _sortedPermutation;
    private object? _sortedPermutationBuildLock;
    private int _sortedPermutationBuildCount;

    // Number of times the sort permutation was built on this segment (read by tests).
    internal int SortedPermutationBuildCount => Volatile.Read(ref _sortedPermutationBuildCount);

    // True once the sort permutation exists. Reading it never starts the build.
    internal bool IsSortedPermutationBuilt => Volatile.Read(ref _sortedPermutation) is not null;

    internal IndexSegment(
        int firstOrdinal,
        int[] recordIds,
        Dictionary<string, WordPostings> exactPostings,
        char[] wordsArray,
        int[] postingDocIds,
        int[] postingOffsets,
        int[] wordEnds,
        int[] wordLengths,
        int[] postingCounts,
        string[] sortTexts,
        Dictionary<string, long[]> facetColumns)
    {
        FirstOrdinal = firstOrdinal;
        RecordIds = recordIds;
        ExactPostings = exactPostings;
        WordsArray = wordsArray;
        PostingDocIds = postingDocIds;
        PostingOffsets = postingOffsets;
        WordEnds = wordEnds;
        WordLengths = wordLengths;
        PostingCounts = postingCounts;
        SortTexts = sortTexts;
        FacetColumns = facetColumns;
    }

    // i ascends, so every list holds ascending token indices and is length-descending (Data layout above).
    private Dictionary<int, int[]> BuildBigramWordIndices()
    {
        var bigramLists = new Dictionary<int, List<int>>();
        for (int i = 0; i < WordLengths.Length; i++)
        {
            var word = WordAt(i);
            // Every int is a valid packed bigram (U+FFFF U+FFFF packs to -1), so "no previous bigram" is a long.
            long prev = long.MinValue;
            for (int j = 0; j + 1 < word.Length; j++)
            {
                int bigram = (word[j] << 16) | word[j + 1];
                if (bigram == prev) continue; // skip a bigram repeated right away (aaa)
                prev = bigram;
                if (!bigramLists.TryGetValue(bigram, out var list))
                    bigramLists[bigram] = list = [];
                if (list.Count == 0 || list[^1] != i)
                    list.Add(i);
            }
        }

        var built = new Dictionary<int, int[]>(bigramLists.Count);
        foreach (var (bigram, list) in bigramLists)
            built[bigram] = list.ToArray();

        Interlocked.Increment(ref _bigramBuildCount);
        return built;
    }

    // The sort permutation: the ordinals of the segment in natural order (NaturalOrder), permutation[rank] = ordinal.
    // Built on first use (Thread safety above).
    internal int[] GetSortedPermutation()
        => Volatile.Read(ref _sortedPermutation)
           ?? LazyInitializer.EnsureInitialized(ref _sortedPermutation, ref _sortedPermutationBuildLock, BuildSortedPermutation);

    private int[] BuildSortedPermutation()
    {
        var computed = new int[RecordIds.Length];
        for (int i = 0; i < computed.Length; i++)
            computed[i] = FirstOrdinal + i;

        NaturalOrder.Sort(computed, this, null);

        Interlocked.Increment(ref _sortedPermutationBuildCount);
        return computed;
    }
}
