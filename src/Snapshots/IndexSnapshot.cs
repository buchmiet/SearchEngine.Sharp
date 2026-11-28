using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// A base segment, an optional delta segment and the sorted list of dead base ordinals. IndexSnapshotBuilder builds a
// snapshot with only a base. IndexUpdater adds the delta and the dead ordinals, and merges them into a new base at
// IndexUpdater.MergeThreshold.
//
// ── Data layout ──
// The base holds ordinals 0 .. Base.DocumentCount - 1 and the delta the ordinals right after it, so one bitset with
// one bit per ordinal covers both and the operators of a query combine them without knowing about segments
// (IndexSegment, Data layout, has the layout of one segment). A dead ordinal is a base document that was removed or
// replaced after the base was built. A query clears the dead bits once, before anything reads hits out of its bitset
// (RemoveDeadOrdinals); operators work bit by bit, so the live bits are right whatever the dead bits hold until then.
// The paths that read posting lists directly skip the dead ordinals (LivePostings).
//
// ── Thread safety ──
// A snapshot never changes after construction except for derived structures built on first use. The segments build
// theirs (IndexSegment, Thread safety); the snapshot adds the natural order of the delta (DeltaOrder), built the same
// way. The distinct-token count (UniqueWordCount) takes no lock: concurrent first readers may each count it and write
// the same value.

/// <summary>
/// An immutable index built by <see cref="IndexSnapshotBuilder"/> or an <see cref="IndexUpdater"/>. Any number of
/// threads may query one snapshot at once.
/// </summary>
/// <remarks>
/// The content never changes after construction. The lookup structures for <see cref="WordMatchMethod.Within"/>
/// terms and for <see cref="SearchSortMode.NaturalSortAscending"/> are built on first use; concurrent first callers
/// wait for that build. A snapshot that an <see cref="IndexUpdater"/> publishes after a small change shares most of
/// its content, and those structures, with the snapshot before it.
/// </remarks>
public sealed class IndexSnapshot
{
    /// <summary>
    /// Gets a shared empty snapshot that uses <see cref="SearchTokenization.Default"/>.
    /// </summary>
    public static readonly IndexSnapshot Empty = new(IndexSegment.Empty, null, [], SearchTokenization.Default);

    // An empty snapshot that keeps the given tokenization, so an index emptied by its updater still carries the preset
    // it was configured with. The default preset shares Empty.
    internal static IndexSnapshot EmptyFor(SearchTokenization tokenization)
        => ReferenceEquals(tokenization, SearchTokenization.Default) ? Empty : new(IndexSegment.Empty, null, [], tokenization);

    // The segment that holds ordinals from 0.
    internal readonly IndexSegment Base;

    // The entries added or replaced since the base was built, or null when there are none.
    internal readonly IndexSegment? Delta;

    // Base ordinals whose documents were removed or replaced since the base was built, ascending.
    internal readonly int[] DeadOrdinals;

    // The base, then the delta when there is one.
    internal readonly IndexSegment[] Segments;

    // The number of ordinals, dead ones included: the length of a query bitset.
    internal int OrdinalCount { get; }

    /// <summary>Gets the preset this snapshot was built with. Every query on the snapshot uses it.</summary>
    public SearchTokenization Tokenization { get; }

    /// <summary>Gets the number of documents in the snapshot.</summary>
    public int DocumentCount { get; }

    /// <summary>Gets the number of distinct tokens across all documents of the snapshot.</summary>
    /// <remarks>Counted on first read when an <see cref="IndexUpdater"/> published the snapshot after a change that
    /// did not run a full build.</remarks>
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

    internal IndexSnapshot(IndexSegment @base, IndexSegment? delta, int[] deadOrdinals, SearchTokenization tokenization)
    {
        Base = @base;
        Delta = delta;
        DeadOrdinals = deadOrdinals;
        Segments = delta is null ? [@base] : [@base, delta];
        Tokenization = tokenization;
        OrdinalCount = @base.DocumentCount + (delta?.DocumentCount ?? 0);
        DocumentCount = OrdinalCount - deadOrdinals.Length;
    }

    // The segment that holds the ordinal.
    internal IndexSegment SegmentOf(int ordinal) => Delta is not null && ordinal >= Delta.FirstOrdinal ? Delta : Base;

    internal int RecordIdAt(int ordinal)
    {
        var segment = SegmentOf(ordinal);
        return segment.RecordIds[ordinal - segment.FirstOrdinal];
    }

    // Ordinals of the live documents that contain exactly this token, ascending.
    internal LivePostings PostingsOf(string word)
        => new(Base.PostingsOf(word), DeadOrdinals, Delta is null ? default : Delta.PostingsOf(word));

    // Clears the bits of the dead ordinals, once per query (Data layout above).
    internal void RemoveDeadOrdinals(FastBitSet results) => results.RemoveAll(DeadOrdinals);

    private DeltaNaturalOrder? _deltaNaturalOrder;
    private object? _deltaNaturalOrderBuildLock;

    // Where the delta documents go in the sort permutation of the base; null when there is no delta. Built on first
    // use (Thread safety above); it builds the permutation of the base, which outlives this snapshot.
    internal DeltaNaturalOrder? DeltaOrder
        => Delta is null
            ? null
            : Volatile.Read(ref _deltaNaturalOrder)
              ?? LazyInitializer.EnsureInitialized(ref _deltaNaturalOrder, ref _deltaNaturalOrderBuildLock, BuildDeltaNaturalOrder);

    // Every publish of a change makes a new snapshot, so this runs once per snapshot that a natural sort walks: one key
    // per delta document, then NaturalOrder.CountBefore (its cost is noted there).
    private DeltaNaturalOrder BuildDeltaNaturalOrder()
    {
        var delta = Delta!;
        var ordinals = new int[delta.DocumentCount];
        for (int i = 0; i < ordinals.Length; i++)
            ordinals[i] = delta.FirstOrdinal + i;
        var keys = new string[ordinals.Length];
        NaturalOrder.Sort(ordinals, delta, null, keys);

        var ids = new int[ordinals.Length];
        for (int i = 0; i < ordinals.Length; i++)
            ids[i] = delta.RecordIds[ordinals[i] - delta.FirstOrdinal];

        var baseRanks = new int[ordinals.Length];
        NaturalOrder.CountBefore(Base.GetSortedPermutation(), Base, keys, ids, baseRanks);
        return new(ordinals, baseRanks);
    }

    // A base token counts while one of its documents is live; a delta token counts unless the base counted it.
    private int CountUniqueWords()
    {
        if (Delta is null && DeadOrdinals.Length == 0)
            return Base.WordCount;

        int count = 0;
        for (int i = 0; i < Base.WordCount; i++)
        {
            if (HasLiveDocument(Base.PostingsAt(i)))
                count++;
        }

        if (Delta is not null)
        {
            foreach (string word in Delta.ExactPostings.Keys)
            {
                if (!HasLiveDocument(Base.PostingsOf(word)))
                    count++;
            }
        }

        return count;
    }

    private bool HasLiveDocument(ReadOnlySpan<int> basePostings)
    {
        foreach (int ordinal in basePostings)
        {
            if (Array.BinarySearch(DeadOrdinals, ordinal) < 0)
                return true;
        }

        return false;
    }
}

// The delta documents in natural order (Ordinals) and, for each, the number of base documents that come before it in
// the sort permutation of the base (BaseRanks, ascending). Dead base documents are counted too: they stay in the
// permutation, and queries skip them because their bits are cleared.
internal sealed class DeltaNaturalOrder(int[] ordinals, int[] baseRanks)
{
    internal int[] Ordinals { get; } = ordinals;

    internal int[] BaseRanks { get; } = baseRanks;
}
