using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// ── Data layout ──────────────────────────────────────────────────────────────
//
// All unique words are packed consecutively into a single char[] (WordsArray).
// Parallel arrays indexed by word ordinal i describe each word's slice:
//
//   WordLengths[i]  : length of word i in characters
//   WordEnds[i]     : exclusive end offset in WordsArray, so word i occupies
//                     WordsArray[ WordEnds[i]-WordLengths[i] .. WordEnds[i] )
//
// Words are stored in descending order of length. This allows the infix search
// loop to stop as soon as WordLengths[i] < queryLength (early exit).
//
// Posting lists (which documents contain each word) are packed into PostingDocIds[].
// Parallel arrays describe each word's slice of that flat list:
//
//   PostingOffsets[i]  : start offset in PostingDocIds
//   PostingCounts[i]   : number of entries
//
// PostingDocIds contains document *ordinals* of the snapshot, not the caller-supplied IDs, in ascending order. The
// documents of a segment have the ordinals FirstOrdinal .. FirstOrdinal + DocumentCount - 1; the per-document arrays
// (RecordIds, SortTexts, facet columns) are indexed by ordinal - FirstOrdinal.
//
// ── Thread safety ─────────────────────────────────────────────────────────────
//
// A segment is immutable after construction except for two derived structures built on first use: the bigram
// index and the natural-sort permutation. Each is built at most once per segment: the first caller builds it under a
// per-structure lock while concurrent first callers wait on that lock (they block instead of spinning or building a
// copy). A build that throws publishes nothing, so the next caller builds again. Nothing is built at publish time.
// The base segment of an index outlives many snapshots (see IndexSnapshot), so its structures survive the publishes
// of small changes.

/// <summary>
/// One immutable part of an <see cref="IndexSnapshot"/>: the inverted index, sort texts and facet columns of a set of
/// documents, built by <see cref="IndexSnapshotBuilder"/>.
/// </summary>
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

    // Caller-supplied integer IDs in document order: RecordIds[o - FirstOrdinal] is the id of ordinal o.
    internal readonly int[] RecordIds;

    // Fast path for exact single-word queries: word string → (offset, count) in PostingDocIds.
    internal readonly Dictionary<string, WordPostings> ExactPostings;

    // All unique words concatenated into one char[]. Navigate using WordEnds + WordLengths.
    internal readonly char[] WordsArray;

    // Flat posting list: document ordinals for every word, packed end-to-end.
    // Navigate using PostingOffsets + PostingCounts.
    internal readonly int[] PostingDocIds;

    // PostingOffsets[i]: start of word i's posting list in PostingDocIds.
    internal readonly int[] PostingOffsets;

    // WordEnds[i]: exclusive end of word i in WordsArray. Word i occupies
    // WordsArray[ WordEnds[i]-WordLengths[i] .. WordEnds[i] ).
    internal readonly int[] WordEnds;

    // WordLengths[i]: character length of word i. Stored in descending order,
    // enabling early exit in MatchWithin: stop when WordLengths[i] < queryLength.
    internal readonly int[] WordLengths;

    // PostingCounts[i]: number of documents that contain word i.
    internal readonly int[] PostingCounts;

    // Sort texts parallel to RecordIds, used to build the sorted permutation on demand.
    internal readonly string[] SortTexts;

    // Facet columns parallel to RecordIds. A facet that no document of the segment carries has no column.
    internal readonly Dictionary<string, long[]> FacetColumns;

    internal int DocumentCount => RecordIds.Length;

    // One past the ordinal of the last document.
    internal int EndOrdinal => FirstOrdinal + RecordIds.Length;

    internal int WordCount => WordLengths.Length;

    // Word i, read from WordsArray.
    internal ReadOnlySpan<char> WordAt(int wordIndex)
        => WordsArray.AsSpan(WordEnds[wordIndex] - WordLengths[wordIndex], WordLengths[wordIndex]);

    // Ordinals of the documents that contain word i.
    internal ReadOnlySpan<int> PostingsAt(int wordIndex)
        => PostingDocIds.AsSpan(PostingOffsets[wordIndex], PostingCounts[wordIndex]);

    // Ordinals of the documents that contain exactly this word; empty when the word is not indexed.
    internal ReadOnlySpan<int> PostingsOf(string word)
        => ExactPostings.TryGetValue(word, out var postings) ? PostingDocIds.AsSpan(postings.Offset, postings.Count) : default;

    // Bigram index: first two characters of query packed as (char0 << 16 | char1)
    // → int[] of word indices that contain that bigram, in length-descending order.
    // Used by MatchWithin to skip words that cannot possibly match (pruning).
    // Built once on first use (only Within queries need it), see Thread safety above.
    private Dictionary<int, int[]>? _bigramWordIndices;
    private object? _bigramBuildLock;
    private int _bigramBuildCount;

    internal Dictionary<int, int[]> BigramWordIndices
        => Volatile.Read(ref _bigramWordIndices)
           ?? LazyInitializer.EnsureInitialized(ref _bigramWordIndices, ref _bigramBuildLock, BuildBigramWordIndices);

    // Number of times the bigram index was built on this segment (tests: at most once).
    internal int BigramBuildCount => Volatile.Read(ref _bigramBuildCount);

    // Index permutation for NaturalSortAscending, built once on first use (see GetSortedPermutation).
    private int[]? _sortedPermutation;
    private object? _sortedPermutationBuildLock;
    private int _sortedPermutationBuildCount;

    // Number of times the sort permutation was built on this segment (tests: at most once).
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

    // Words are stored in descending length order, so iterating i = 0..N-1 adds word indices in
    // the same order to every list: each candidate list is length-descending and MatchWithin can
    // stop at the first word shorter than the query.
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

    // Returns the ordinals of the segment sorted by natural sort key, built once on first use:
    // sortedPermutation[rank] = ordinal.
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
