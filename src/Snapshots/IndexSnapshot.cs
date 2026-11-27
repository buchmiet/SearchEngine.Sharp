using SearchEngine.Index;
using SearchEngine.Sorting;

namespace SearchEngine.Snapshots;

// ── Segments ──────────────────────────────────────────────────────────────────
//
// A snapshot is a base segment, an optional delta segment and the sorted list of dead base ordinals (IndexSegment
// has the data layout). The base holds ordinals 0 .. Base.DocumentCount - 1 and the delta the ordinals right after
// it, so one bitset with one bit per ordinal covers both and the operators of a query combine them without knowing
// about segments. A dead ordinal is a base document that was removed or replaced after the base was built; queries
// clear the dead bits once, from the final bitset (operators work bit by bit, so the live bits are right whatever
// the dead bits hold until then), and the paths that read posting lists directly skip them (LivePostings).
//
// IndexSnapshotBuilder builds a snapshot with only a base. IndexUpdater keeps the base of the previous snapshot,
// builds a new delta from the entries changed since the base was built and adds the ordinals that died, so a change
// costs a build of the changed entries instead of the whole index; it rebuilds the base from all entries (a merge)
// when the delta and the dead ordinals grow too large.
//
// ── Thread safety ─────────────────────────────────────────────────────────────
//
// A snapshot never changes after construction except for lazily built derived structures: the bigram index and the
// sort permutation of each segment (IndexSegment), and here the natural order of the delta and the distinct-token
// count. Each structure is built at most once per owner; concurrent first callers wait for that build. Any number of
// threads may read a snapshot concurrently. IndexSnapshotProvider swaps the reference atomically
// (Interlocked.Exchange), so readers always see a consistent snapshot.

/// <summary>
/// An immutable index built by <see cref="IndexSnapshotBuilder"/> or an <see cref="IndexUpdater"/>. Any number of
/// threads may query one snapshot at once.
/// </summary>
/// <remarks>
/// The content never changes after construction. Lookup structures for <see cref="WordMatchMethod.Within"/> terms
/// and for <see cref="SearchSortMode.NaturalSortAscending"/> are built on first use instead; concurrent first callers
/// wait for that build. A snapshot that an <see cref="IndexUpdater"/> publishes after a small change shares most of
/// its content, and those structures, with the snapshot before it.
/// </remarks>
public sealed class IndexSnapshot
{
    /// <summary>
    /// Gets a shared empty snapshot that uses <see cref="SearchTokenization.Default"/>.
    /// </summary>
    public static readonly IndexSnapshot Empty = new(IndexSegment.Empty, null, [], SearchTokenization.Default);

    /// <summary>
    /// Empty snapshot that keeps the given tokenization, so an index emptied by its updater still
    /// carries the preset it was configured with. The default preset shares <see cref="Empty"/>.
    /// </summary>
    internal static IndexSnapshot EmptyFor(SearchTokenization tokenization)
        => ReferenceEquals(tokenization, SearchTokenization.Default) ? Empty : new(IndexSegment.Empty, null, [], tokenization);

    // The segment that holds ordinals from 0, built from all entries at the last merge.
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
    /// <remarks>Counted on first read when the snapshot was published by an <see cref="IndexUpdater"/> after a
    /// change that did not rebuild the whole index.</remarks>
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

    // Ordinals of the live documents that contain exactly this word, ascending.
    internal LivePostings PostingsOf(string word)
        => new(Base.PostingsOf(word), DeadOrdinals, Delta is null ? default : Delta.PostingsOf(word));

    // Clears the bits of the dead ordinals: the last step of every query that evaluates into a bitset.
    internal void RemoveDeadOrdinals(FastBitSet results) => results.RemoveAll(DeadOrdinals);

    // ── Natural order of the delta ──

    private DeltaNaturalOrder? _deltaNaturalOrder;
    private object? _deltaNaturalOrderBuildLock;

    // Where the delta documents go in the sort permutation of the base; null when there is no delta. Built on first
    // use; it builds the permutation of the base, which outlives this snapshot.
    internal DeltaNaturalOrder? DeltaOrder
        => Delta is null
            ? null
            : Volatile.Read(ref _deltaNaturalOrder)
              ?? LazyInitializer.EnsureInitialized(ref _deltaNaturalOrder, ref _deltaNaturalOrderBuildLock, BuildDeltaNaturalOrder);

    private DeltaNaturalOrder BuildDeltaNaturalOrder()
    {
        var delta = Delta!;
        var ordinals = new int[delta.DocumentCount];
        for (int i = 0; i < ordinals.Length; i++)
            ordinals[i] = delta.FirstOrdinal + i;
        NaturalOrder.Sort(ordinals, delta, null);

        var permutation = Base.GetSortedPermutation();
        var baseRanks = new int[ordinals.Length];
        for (int i = 0; i < ordinals.Length; i++)
        {
            int local = ordinals[i] - delta.FirstOrdinal;
            baseRanks[i] = NaturalOrder.CountBefore(permutation, Base, NaturalSortKeyBuilder.Build(delta.SortTexts[local]), delta.RecordIds[local]);
        }

        return new(ordinals, baseRanks);
    }

    // ── Distinct tokens ──

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
