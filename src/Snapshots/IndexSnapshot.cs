using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// An ordered list of immutable segments and, for each, the set of its documents that were removed or replaced after
// it was built. IndexSnapshotBuilder builds a snapshot with one segment. IndexUpdater publishes a new segment for
// every change and merges neighbouring segments in the background (IndexUpdater, Cost).
//
// ── Data layout ──
// The documents of the snapshot have ordinals: the segment i owns the range that starts at Offsets[i], and a segment
// starts on a multiple of 64 so that one bitset with one bit per ordinal covers all segments and the dead documents
// of a segment can be cleared a word at a time. The operators of a query combine the bitsets without knowing about
// segments (IndexSegment, Data layout, has the layout of one segment, whose postings are local). The 0-63 ordinals
// between the end of a segment and the start of the next one belong to no document: like a dead ordinal, a query
// clears them once, before anything reads hits out of its bitset (RemoveDeadOrdinals). Operators work bit by bit, so
// the live bits are right whatever those bits hold until then. The paths that read posting lists directly skip the
// dead documents (LivePostings).
//
// ── Thread safety ──
// A snapshot never changes after construction except for derived structures built on first use. The segments build
// theirs (IndexSegment, Thread safety); the snapshot adds the natural order of the documents outside the largest
// segment (OtherOrder), built the same way. The distinct-token count (UniqueWordCount) takes no lock: concurrent
// first readers may each count it and write the same value.

/// <summary>
/// An immutable index built by <see cref="IndexSnapshotBuilder"/> or an <see cref="IndexUpdater"/>. Any number of
/// threads may query one snapshot at once.
/// </summary>
/// <remarks>
/// The content never changes after construction. The lookup structures for <see cref="WordMatchMethod.Within"/>
/// terms and for <see cref="SearchSortMode.NaturalSortAscending"/> are built on first use; concurrent first callers
/// wait for that build. A snapshot that an <see cref="IndexUpdater"/> publishes after a change shares almost all of
/// its content, and those structures, with the snapshot before it.
/// </remarks>
public sealed class IndexSnapshot
{
    /// <summary>
    /// Gets a shared empty snapshot that uses <see cref="SearchTokenization.Default"/>.
    /// </summary>
    public static readonly IndexSnapshot Empty = new([], [], SearchTokenization.Default);

    // An empty snapshot that keeps the given tokenization, so an index emptied by its updater still carries the preset
    // it was configured with. The default preset shares Empty.
    internal static IndexSnapshot EmptyFor(SearchTokenization tokenization)
        => ReferenceEquals(tokenization, SearchTokenization.Default) ? Empty : new([], [], tokenization);

    // The segments in snapshot order. Every segment holds at least one live document.
    internal readonly IndexSegment[] Segments;

    // The ordinal of the first document of each segment, ascending, a multiple of 64.
    internal readonly int[] Offsets;

    // The dead documents of each segment, or null for a segment without any.
    internal readonly DeadSet?[] Dead;

    // The index of the largest segment: the one whose sort permutation the natural order walks (OtherOrder).
    internal readonly int PrimaryIndex;

    // The number of ordinals, dead and unused ones included: the length of a query bitset.
    internal int OrdinalCount { get; }

    /// <summary>Gets the preset this snapshot was built with. Every query on the snapshot uses it.</summary>
    public SearchTokenization Tokenization { get; }

    /// <summary>Gets the number of documents in the snapshot.</summary>
    public int DocumentCount { get; }

    /// <summary>Gets the number of distinct tokens across all documents of the snapshot.</summary>
    /// <remarks>Counted on first read when the snapshot has more than one segment or a dead document.</remarks>
    public int UniqueWordCount
    {
        get
        {
            int count = Volatile.Read(ref _uniqueWordCount);
            if (count < 0)
                Volatile.Write(ref _uniqueWordCount, count = CountUniqueWords());
            return count;
        }
    }

    private int _uniqueWordCount = -1;

    // The arrays are kept, not copied: the caller must not change them afterwards.
    internal IndexSnapshot(IndexSegment[] segments, DeadSet?[] dead, SearchTokenization tokenization)
    {
        Segments = segments;
        Dead = dead;
        Tokenization = tokenization;
        Offsets = new int[segments.Length];
        int next = 0;
        int documents = 0;
        int largest = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            Offsets[i] = next;
            int length = segments[i].DocumentCount;
            next += (length + 63) & ~63;
            documents += length - (dead[i]?.Count ?? 0);
            if (length > segments[largest].DocumentCount)
                largest = i;
        }

        PrimaryIndex = largest;
        OrdinalCount = segments.Length == 0 ? 0 : Offsets[^1] + segments[^1].DocumentCount;
        DocumentCount = documents;
    }

    // The largest segment; its sort permutation is the backbone of the natural order.
    internal IndexSegment Primary => Segments[PrimaryIndex];

    // The index in Segments of the segment that holds the ordinal.
    internal int SegmentIndexOf(int ordinal)
    {
        var offsets = Offsets;
        if (offsets.Length == 1)
            return 0;

        int low = 0;
        int high = offsets.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (offsets[middle] <= ordinal)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }

    internal int RecordIdAt(int ordinal)
    {
        int s = SegmentIndexOf(ordinal);
        return Segments[s].RecordIds[ordinal - Offsets[s]];
    }

    // Ordinals of the live documents that contain exactly this token, ascending.
    internal LivePostings PostingsOf(string word) => new(this, word);

    // Clears the bits of the dead documents and of the unused ordinals between segments, once per query (Data layout
    // above).
    internal void RemoveDeadOrdinals(FastBitSet results)
    {
        for (int s = 0; s < Segments.Length; s++)
        {
            int length = Segments[s].DocumentCount;
            int firstWord = Offsets[s] >> 6;
            int words = (length + 63) >> 6;
            if (Dead[s] is { } dead)
            {
                for (int c = 0; c < dead.ChunkCount; c++)
                {
                    if (dead.ChunkAt(c) is not { } chunk)
                        continue;

                    int chunkWords = Math.Min(DeadSet.ChunkWords, words - c * DeadSet.ChunkWords);
                    results.ExceptWithWords(firstWord + c * DeadSet.ChunkWords, chunk.AsSpan(0, chunkWords));
                }
            }

            // The last segment ends at the end of the bitset; any other one is followed by unused ordinals.
            int used = length & 63;
            if (used != 0 && s < Segments.Length - 1)
                results.KeepBitsOfWord(firstWord + words - 1, (1UL << used) - 1);
        }
    }

    // The documents outside the largest segment in natural order, placed among the documents of its sort permutation.
    private OtherNaturalOrder? _otherOrder;
    private object? _otherOrderBuildLock;

    // Null when the snapshot has one segment. Built on first use (Thread safety above); it builds the permutation
    // of the largest segment, which outlives this snapshot.
    internal OtherNaturalOrder? OtherOrder
        => Segments.Length < 2
            ? null
            : Volatile.Read(ref _otherOrder)
              ?? LazyInitializer.EnsureInitialized(ref _otherOrder, ref _otherOrderBuildLock, BuildOtherNaturalOrder);

    // Every publish of a change makes a new snapshot, so this runs once per snapshot that a natural sort walks: one key
    // per live document outside the largest segment, then NaturalOrder.CountBefore (its cost is noted there).
    private OtherNaturalOrder BuildOtherNaturalOrder()
    {
        int count = 0;
        for (int s = 0; s < Segments.Length; s++)
        {
            if (s != PrimaryIndex)
                count += Segments[s].DocumentCount - (Dead[s]?.Count ?? 0);
        }

        var ordinals = new int[count];
        int n = 0;
        for (int s = 0; s < Segments.Length; s++)
        {
            if (s == PrimaryIndex)
                continue;

            for (int local = 0; local < Segments[s].DocumentCount; local++)
            {
                if (Dead[s] is not { } dead || !dead.Contains(local))
                    ordinals[n++] = Offsets[s] + local;
            }
        }

        var keys = new string[count];
        NaturalOrder.Sort(ordinals, this, keys);

        var ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = RecordIdAt(ordinals[i]);

        var primaryRanks = new int[count];
        NaturalOrder.CountBefore(Primary.GetSortedPermutation(), Primary, keys, ids, primaryRanks);
        return new(ordinals, primaryRanks);
    }

    // A token counts while one of its documents is live.
    private int CountUniqueWords()
    {
        if (Segments.Length == 1 && Dead[0] is null)
            return Segments[0].WordCount;

        HashSet<string> words = [with(StringComparer.Ordinal)];
        for (int s = 0; s < Segments.Length; s++)
        {
            var segment = Segments[s];
            var dead = Dead[s];
            foreach (var (word, range) in segment.ExactPostings)
            {
                if (dead is null)
                {
                    words.Add(word);
                    continue;
                }

                var postings = segment.PostingDocIds.AsSpan(range.Offset, range.Count);
                foreach (int local in postings)
                {
                    if (!dead.Contains(local))
                    {
                        words.Add(word);
                        break;
                    }
                }
            }
        }

        return words.Count;
    }
}

// The live documents outside the largest segment in natural order (Ordinals, snapshot ordinals) and, for each, the
// number of documents of the sort permutation of the largest segment that come before it (PrimaryRanks, ascending).
// Dead documents of the largest segment are counted too: they stay in the permutation, and queries skip them because
// their bits are cleared.
internal sealed class OtherNaturalOrder(int[] ordinals, int[] primaryRanks)
{
    internal int[] Ordinals { get; } = ordinals;

    internal int[] PrimaryRanks { get; } = primaryRanks;
}
