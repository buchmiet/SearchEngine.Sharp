using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// ── Data layout ──────────────────────────────────────────────────────────────
//
// All unique words are packed consecutively into a single char[] (WordsArray).
// Parallel arrays indexed by word ordinal i describe each word's slice:
//
//   WordLengths[i]  — length of word i in characters
//   WordEnds[i]     — exclusive end offset in WordsArray, so word i occupies
//                     WordsArray[ WordEnds[i]-WordLengths[i] .. WordEnds[i] )
//
// Words are stored in descending order of length. This allows the infix search
// loop to stop as soon as WordLengths[i] < queryLength (early exit).
//
// Posting lists (which documents contain each word) are packed into PostingDocIds[].
// Parallel arrays describe each word's slice of that flat list:
//
//   PostingOffsets[i]  — start offset in PostingDocIds
//   PostingCounts[i]   — number of entries
//
// PostingDocIds contains document *ordinals* (positions in RecordIds[]), not
// the caller-supplied IDs. The final translation ordinal → caller ID happens
// once at result materialisation in SearchEngineSharp.Find.
//
// ── Thread safety ─────────────────────────────────────────────────────────────
//
// IndexSnapshot is fully immutable after construction. Any number of threads
// may read it concurrently. IndexSnapshotProvider swaps the reference atomically
// (Interlocked.Exchange) so readers always see a consistent snapshot.

/// <summary>
/// Immutable snapshot of the search index.
/// Thread-safe for concurrent reads. Never modified after construction.
/// </summary>
public sealed class IndexSnapshot
{
    /// <summary>
    /// Empty snapshot singleton.
    /// </summary>
    public static readonly IndexSnapshot Empty = new(
        recordIds: [],
        exactPostings: [],
        wordsArray: [],
        postingDocIds: [],
        postingOffsets: [],
        wordEnds: [],
        wordLengths: [],
        postingCounts: [],
        bigramWordIndices: [],
        sortTexts: [],
        sortedPermutation: [],
        facetColumns: [],
        tokenization: SearchTokenization.Default,
        documentCount: 0,
        uniqueWordCount: 0
    );

    // Caller-supplied integer IDs in document-ordinal order.
    // Index i → the ID that was passed to IndexUpdater for document i.
    internal readonly int[] RecordIds;

    // Fast path for exact single-word queries: word string → (offset, count) in PostingDocIds.
    internal readonly Dictionary<string, WordPostings> ExactPostings;

    // All unique words concatenated into one char[]. Navigate using WordEnds + WordLengths.
    internal readonly char[] WordsArray;

    // Flat posting list: document ordinals for every word, packed end-to-end.
    // Navigate using PostingOffsets + PostingCounts.
    internal readonly int[] PostingDocIds;

    // PostingOffsets[i] — start of word i's posting list in PostingDocIds.
    internal readonly int[] PostingOffsets;

    // WordEnds[i] — exclusive end of word i in WordsArray. Word i occupies
    // WordsArray[ WordEnds[i]-WordLengths[i] .. WordEnds[i] ).
    internal readonly int[] WordEnds;

    // WordLengths[i] — character length of word i. Stored in descending order,
    // enabling early exit in MatchWithin: stop when WordLengths[i] < queryLength.
    internal readonly int[] WordLengths;

    // PostingCounts[i] — number of documents that contain word i.
    internal readonly int[] PostingCounts;

    // Bigram index: first two characters of query packed as (char0 << 16 | char1)
    // → int[] of word indices that contain that bigram, in length-descending order.
    // Used by MatchWithin to skip words that cannot possibly match (pruning).
    internal readonly Dictionary<int, int[]> BigramWordIndices;

    // Original sort texts parallel to RecordIds, used to build the sorted permutation on demand.
    private readonly string[] _sortTexts;

    // Lazily computed index permutation for NaturalSortAscending.
    // Computed once on first use via Interlocked.CompareExchange (see GetSortedPermutation).
    private int[]? _sortedPermutation;

    // Facet columns parallel to RecordIds/document ordinals. Empty when no facets are indexed.
    internal readonly Dictionary<string, long[]> FacetColumns;

    /// <summary>Tokenization preset used when this snapshot was built.</summary>
    public SearchTokenization Tokenization { get; }

    public int DocumentCount { get; }
    public int UniqueWordCount { get; }

    internal IndexSnapshot(
        int[] recordIds,
        Dictionary<string, WordPostings> exactPostings,
        char[] wordsArray,
        int[] postingDocIds,
        int[] postingOffsets,
        int[] wordEnds,
        int[] wordLengths,
        int[] postingCounts,
        Dictionary<int, int[]> bigramWordIndices,
        string[] sortTexts,
        int[]? sortedPermutation,
        Dictionary<string, long[]> facetColumns,
        SearchTokenization tokenization,
        int documentCount,
        int uniqueWordCount)
    {
        RecordIds = recordIds;
        ExactPostings = exactPostings;
        WordsArray = wordsArray;
        PostingDocIds = postingDocIds;
        PostingOffsets = postingOffsets;
        WordEnds = wordEnds;
        WordLengths = wordLengths;
        PostingCounts = postingCounts;
        BigramWordIndices = bigramWordIndices;
        _sortTexts = sortTexts;
        _sortedPermutation = sortedPermutation;
        FacetColumns = facetColumns;
        Tokenization = tokenization;
        DocumentCount = documentCount;
        UniqueWordCount = uniqueWordCount;
    }

    // Returns (or lazily builds) an array of document ordinals sorted by natural sort key.
    // Sorted ordinals are computed once and cached — Interlocked.CompareExchange ensures
    // that concurrent first callers produce the same result and only one wins the write.
    // The array is an index permutation: sortedPermutation[rank] = ordinal in RecordIds.
    internal int[] GetSortedPermutation()
    {
        if (_sortedPermutation is not null)
            return _sortedPermutation;

        var computed = new int[RecordIds.Length];
        for (int i = 0; i < computed.Length; i++)
            computed[i] = i;

        var naturalSortKeys = new string[_sortTexts.Length];
        for (int i = 0; i < _sortTexts.Length; i++)
            naturalSortKeys[i] = NaturalSortKeyBuilder.Build(_sortTexts[i]);

        Array.Sort(computed, (a, b) =>
        {
            int cmp = string.Compare(naturalSortKeys[a], naturalSortKeys[b], StringComparison.Ordinal);
            return cmp != 0 ? cmp : RecordIds[a].CompareTo(RecordIds[b]); // stable tie-break by ID
        });

        Interlocked.CompareExchange(ref _sortedPermutation, computed, null);
        return _sortedPermutation!;
    }
}
