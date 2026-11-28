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
// PostingDocIds holds local ordinals, not record ids, ascending within each token: the documents of a segment are
// numbered 0 .. DocumentCount - 1, and the per-document arrays (RecordIds, SortTexts, FacetColumns) are indexed by
// that number. A segment knows nothing about its place in a snapshot, so the same segment object can sit at a
// different place in the next snapshot after a merge (IndexSnapshot, Data layout, adds the offset).
//
// ── Thread safety ──
// A segment is immutable after construction except for three derived structures built on first use: the bigram index,
// the tokens in text order and the sort permutation. Each is built at most once per segment: the first caller builds it under a per-structure
// lock while concurrent first callers wait on that lock. A build that throws publishes nothing, so the next caller
// builds again. Nothing is built at publish time. A segment outlives many snapshots
// (IndexSnapshot), so its structures survive the publishes of changes that do not touch it.
internal sealed class IndexSegment
{
    internal static readonly IndexSegment Empty = new(
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

    // Token indices sorted ordinally by token text (UTF-16 code units). The tokens that start with a given prefix form one
    // contiguous run, which a glob with a literal prefix visits instead of every token (QueryMatcher.MatchGlob).
    private int[]? _wordsByText;
    private object? _wordsByTextBuildLock;
    private int _wordsByTextBuildCount;

    internal int[] WordsByText
        => Volatile.Read(ref _wordsByText)
           ?? LazyInitializer.EnsureInitialized(ref _wordsByText, ref _wordsByTextBuildLock, BuildWordsByText);

    // Number of times the text order was built on this segment (read by tests).
    internal int WordsByTextBuildCount => Volatile.Read(ref _wordsByTextBuildCount);

    // Sort permutation for NaturalSortAscending (GetSortedPermutation).
    private int[]? _sortedPermutation;
    private object? _sortedPermutationBuildLock;
    private int _sortedPermutationBuildCount;

    // Number of times the sort permutation was built on this segment (read by tests).
    internal int SortedPermutationBuildCount => Volatile.Read(ref _sortedPermutationBuildCount);

    // True once the sort permutation exists. Reading it never starts the build.
    internal bool IsSortedPermutationBuilt => Volatile.Read(ref _sortedPermutation) is not null;

    internal IndexSegment(
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

    // Record id → local ordinal. Built on first use; the updater builds it before it publishes a segment, so a write
    // never pays for it (IndexUpdater).
    private IdIndex? _ids;
    private object? _idsBuildLock;

    internal IdIndex Ids
        => Volatile.Read(ref _ids) ?? LazyInitializer.EnsureInitialized(ref _ids, ref _idsBuildLock, () => new IdIndex(RecordIds));

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

    // Sorts by the first four characters packed into a ulong (a primitive sort), then orders the tokens that share them
    // by full text.
    private int[] BuildWordsByText()
    {
        int count = WordLengths.Length;
        var order = new int[count];
        var keys = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            keys[i] = TextKey(WordAt(i));
        }

        keys.AsSpan().Sort(order.AsSpan());
        for (int start = 0; start < count;)
        {
            int end = start + 1;
            while (end < count && keys[end] == keys[start])
                end++;

            if (end - start > 1)
                order.AsSpan(start, end - start).Sort(new TextOrder(this));
            start = end;
        }

        _wordsByTextBuildCount++; // runs under the build lock (Thread safety above)
        return order;
    }

    // The first four characters, the first in the highest bits; a missing character is 0. Keys that differ order the
    // tokens as the ordinal comparison does, because a shorter token sorts before a longer one with the same start and
    // 0 is the lowest character; equal keys are settled by the full comparison.
    private static ulong TextKey(ReadOnlySpan<char> word)
    {
        ulong key = 0;
        for (int i = 0; i < 4; i++)
            key = (key << 16) | (i < word.Length ? word[i] : (ulong)0);
        return key;
    }

    // The positions in WordsByText of the first token that starts with the prefix and of the first token after the run.
    // A token that is a proper prefix of the prefix sorts before the run, so it is not in it.
    internal (int Start, int End) PrefixRange(ReadOnlySpan<char> prefix)
    {
        var order = WordsByText;
        int start = FirstAtLeast(order, prefix, strict: false);
        int end = FirstAtLeast(order, prefix, strict: true);
        return (start, end);
    }

    // strict false: the first token that is not below the run of the prefix; strict true: the first token above it.
    private int FirstAtLeast(int[] order, ReadOnlySpan<char> prefix, bool strict)
    {
        int low = 0;
        int high = order.Length;
        while (low < high)
        {
            int mid = (int)((uint)(low + high) >> 1);
            var word = WordAt(order[mid]);
            int cmp = word.Length >= prefix.Length
                ? word[..prefix.Length].SequenceCompareTo(prefix)
                : (word.SequenceCompareTo(prefix[..word.Length]) is var c && c != 0 ? c : -1);
            if (strict ? cmp <= 0 : cmp < 0)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private readonly struct TextOrder(IndexSegment segment) : IComparer<int>
    {
        public int Compare(int x, int y) => segment.WordAt(x).SequenceCompareTo(segment.WordAt(y));
    }

    // The sort permutation: the local ordinals of the segment in natural order (NaturalOrder), permutation[rank] = ordinal.
    // Built on first use (Thread safety above).
    internal int[] GetSortedPermutation()
        => Volatile.Read(ref _sortedPermutation)
           ?? LazyInitializer.EnsureInitialized(ref _sortedPermutation, ref _sortedPermutationBuildLock, BuildSortedPermutation);

    private int[] BuildSortedPermutation()
    {
        var computed = new int[RecordIds.Length];
        for (int i = 0; i < computed.Length; i++)
            computed[i] = i;

        NaturalOrder.Sort(computed, this);

        Interlocked.Increment(ref _sortedPermutationBuildCount);
        return computed;
    }
}
