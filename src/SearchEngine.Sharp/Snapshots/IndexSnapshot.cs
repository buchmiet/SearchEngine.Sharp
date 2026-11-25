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
// IndexSnapshot is immutable after construction except for two derived structures built on
// first use: the bigram index and the natural-sort permutation. Each is built at most once per
// snapshot: the first caller builds it under a per-structure lock while concurrent first callers
// wait on that lock (they block instead of spinning or building a copy). A build that throws
// publishes nothing, so the next caller builds again. Nothing is built at publish time: progressive
// ingestion publishes many snapshots that never serve a Within or natural-sort query, and a
// background build would add that cost to every one of them. Any number of threads may read a snapshot
// concurrently. IndexSnapshotProvider swaps the reference atomically (Interlocked.Exchange) so
// readers always see a consistent snapshot.

/// <summary>
/// Immutable snapshot of the search index.
/// Thread-safe for concurrent reads. Its content never changes after construction.
/// </summary>
public sealed class IndexSnapshot
{
    /// <summary>
    /// Empty snapshot singleton.
    /// </summary>
    public static readonly IndexSnapshot Empty = CreateEmpty(SearchTokenization.Default);

    /// <summary>
    /// Empty snapshot that keeps the given tokenization, so an index emptied by its updater still
    /// carries the preset it was configured with. The default preset shares <see cref="Empty"/>.
    /// </summary>
    internal static IndexSnapshot EmptyFor(SearchTokenization tokenization)
        => ReferenceEquals(tokenization, SearchTokenization.Default) ? Empty : CreateEmpty(tokenization);

    private static IndexSnapshot CreateEmpty(SearchTokenization tokenization) => new(
        recordIds: [],
        exactPostings: [],
        wordsArray: [],
        postingDocIds: [],
        postingOffsets: [],
        wordEnds: [],
        wordLengths: [],
        postingCounts: [],
        sortTexts: [],
        facetColumns: [],
        tokenization: tokenization,
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
    // Built once on first use (only Within queries need it), see Thread safety above.
    private Dictionary<int, int[]>? _bigramWordIndices;
    private object? _bigramBuildLock;
    private int _bigramBuildCount;

    internal Dictionary<int, int[]> BigramWordIndices
        => Volatile.Read(ref _bigramWordIndices)
           ?? LazyInitializer.EnsureInitialized(ref _bigramWordIndices, ref _bigramBuildLock, BuildBigramWordIndices);

    // Number of times the bigram index was built on this snapshot (tests: at most once).
    internal int BigramBuildCount => Volatile.Read(ref _bigramBuildCount);

    // Original sort texts parallel to RecordIds, used to build the sorted permutation on demand.
    private readonly string[] _sortTexts;
    internal ReadOnlySpan<string> SortTexts => _sortTexts;
    internal string[] SortTextArray => _sortTexts;

    // Index permutation for NaturalSortAscending, built once on first use (see GetSortedPermutation).
    private int[]? _sortedPermutation;
    private object? _sortedPermutationBuildLock;
    private int _sortedPermutationBuildCount;

    // Number of times the sort permutation was built on this snapshot (tests: at most once).
    internal int SortedPermutationBuildCount => Volatile.Read(ref _sortedPermutationBuildCount);

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
        string[] sortTexts,
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
        _sortTexts = sortTexts;
        FacetColumns = facetColumns;
        Tokenization = tokenization;
        DocumentCount = documentCount;
        UniqueWordCount = uniqueWordCount;
    }

    // Words are stored in descending length order, so iterating i = 0..N-1 adds word indices in
    // the same order to every list: each candidate list is length-descending and MatchWithin can
    // stop at the first word shorter than the query.
    private Dictionary<int, int[]> BuildBigramWordIndices()
    {
        var bigramLists = new Dictionary<int, List<int>>();
        var words = WordsArray.AsSpan();
        for (int i = 0; i < WordLengths.Length; i++)
        {
            var word = words.Slice(WordEnds[i] - WordLengths[i], WordLengths[i]);
            int prev = -1;
            for (int j = 0; j + 1 < word.Length; j++)
            {
                int bigram = (word[j] << 16) | word[j + 1];
                if (bigram == prev) continue; // skip duplicate bigrams within same word
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

    // Returns an array of document ordinals sorted by natural sort key, built once on first use.
    // The array is an index permutation: sortedPermutation[rank] = ordinal in RecordIds.
    internal int[] GetSortedPermutation()
        => Volatile.Read(ref _sortedPermutation)
           ?? LazyInitializer.EnsureInitialized(ref _sortedPermutation, ref _sortedPermutationBuildLock, BuildSortedPermutation);

    private int[] BuildSortedPermutation()
    {
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

        Interlocked.Increment(ref _sortedPermutationBuildCount);
        return computed;
    }
}
