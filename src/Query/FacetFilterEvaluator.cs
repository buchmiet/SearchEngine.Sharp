using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;
using System.Buffers;

namespace SearchEngine.Query;

// A document without a value for a facet has the value 0, also when no document of its segment carries the facet
// (SegmentFilter).
internal static class FacetFilterEvaluator
{
    // results holds no dead ordinals (IndexSnapshot.RemoveDeadOrdinals): its count is compared with the live
    // DocumentCount.
    internal static void Apply(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext qc)
    {
        if (filter.IsEmpty)
            return;

        int hitCount = results.GetTrueCount();
        if (hitCount == 0)
            return;

        if (SelectivityThresholds.UseFacetOnHitsPath(hitCount, snapshot.DocumentCount))
            ApplyOnHitsInPlace(results, filter, snapshot, hitCount);
        else
            ApplyFullScan(results, filter, snapshot, qc);
    }

    // Tests every document against the filter, then intersects: cost grows with the document count.
    internal static void ApplyFullScan(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext qc)
        => results.IntersectWith(BuildMatchingBitSet(filter, snapshot, qc));

    // Tests only the documents that already match the text query: cost grows with the hit count.
    internal static void ApplyOnHitsInPlace(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        int hitCount)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = results.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            var bound = new SnapshotFilter(filter, snapshot);
            results.Clear();
            for (int i = 0; i < count; i++)
            {
                int ordinal = ordinals[i];
                if (bound.Matches(ordinal))
                    results.Add(ordinal);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    // The ordinals whose documents match the filter. Dead ordinals can be set; the caller clears them.
    internal static FastBitSet BuildMatchingBitSet(
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext qc)
    {
        if (filter.IsEmpty)
            return qc.RentAllTrueBitSet();

        var matching = qc.RentEmptyBitSet();
        foreach (var segment in snapshot.Segments)
        {
            var bound = SegmentFilter.Bind(filter, segment);
            if (bound.MatchesNothing)
                continue;

            int first = segment.FirstOrdinal;
            for (int local = 0; local < segment.DocumentCount; local++)
            {
                if (bound.Matches(local))
                    matching.Add(first + local);
            }
        }

        return matching;
    }

    internal static int CountMatchingOrdinals(
        FacetFilter filter,
        IndexSnapshot snapshot,
        LivePostings ordinals)
    {
        if (filter.IsEmpty)
            return ordinals.Count;

        var bound = new SnapshotFilter(filter, snapshot);
        int count = 0;
        foreach (int ordinal in ordinals)
        {
            if (bound.Matches(ordinal))
                count++;
        }

        return count;
    }

    internal static List<int> MaterializeMatchingRecordIds(
        FacetFilter filter,
        IndexSnapshot snapshot,
        LivePostings ordinals)
    {
        if (filter.IsEmpty)
            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals);

        var bound = new SnapshotFilter(filter, snapshot);
        var results = new List<int>();
        foreach (int ordinal in ordinals)
        {
            if (bound.Matches(ordinal))
                results.Add(snapshot.RecordIdAt(ordinal));
        }

        return results;
    }

    // Exhaustive because FacetPredicate is closed; a new kind gives warning CS8509 here.
    private static bool Matches(FacetPredicate predicate, long value) => predicate switch
    {
        FacetRange range => value >= range.MinInclusive && value <= range.MaxInclusive,
        FacetMask mask => (value & mask.MustHave) == mask.MustHave && (value & mask.MustNot) == 0,
    };

    // A filter bound to the facet columns of one segment, read by ordinal - FirstOrdinal. A predicate on a facet
    // without a column is true or false for every document of the segment (value 0): a true one is dropped, a false one
    // makes the segment match nothing.
    private readonly struct SegmentFilter
    {
        private readonly FacetPredicate[]? _predicates;
        private readonly long[][]? _columns;

        private SegmentFilter(FacetPredicate[]? predicates, long[][]? columns)
        {
            _predicates = predicates;
            _columns = columns;
        }

        internal bool MatchesNothing => _predicates is null;

        internal static SegmentFilter Bind(FacetFilter filter, IndexSegment segment)
        {
            ReadOnlySpan<FacetPredicate> all = filter.Predicates;
            var predicates = new List<FacetPredicate>(all.Length);
            var columns = new List<long[]>(all.Length);
            foreach (var predicate in all)
            {
                if (segment.FacetColumns.TryGetValue(predicate.Facet, out long[]? column))
                {
                    predicates.Add(predicate);
                    columns.Add(column);
                }
                else if (!FacetFilterEvaluator.Matches(predicate, 0))
                {
                    return new(null, null);
                }
            }

            return new([.. predicates], [.. columns]);
        }

        internal bool Matches(int local)
        {
            var predicates = _predicates!;
            var columns = _columns!;
            for (int i = 0; i < predicates.Length; i++)
            {
                if (!FacetFilterEvaluator.Matches(predicates[i], columns[i][local]))
                    return false;
            }

            return true;
        }
    }

    // A filter bound to every segment of a snapshot, read by snapshot ordinal.
    private readonly struct SnapshotFilter
    {
        private readonly SegmentFilter _base;
        private readonly SegmentFilter _delta;
        private readonly int _deltaStart;

        internal SnapshotFilter(FacetFilter filter, IndexSnapshot snapshot)
        {
            _base = SegmentFilter.Bind(filter, snapshot.Base);
            if (snapshot.Delta is { } delta)
            {
                _delta = SegmentFilter.Bind(filter, delta);
                _deltaStart = delta.FirstOrdinal;
            }
            else
            {
                _deltaStart = int.MaxValue;
            }
        }

        internal bool Matches(int ordinal)
        {
            if (ordinal >= _deltaStart)
                return !_delta.MatchesNothing && _delta.Matches(ordinal - _deltaStart);

            return !_base.MatchesNothing && _base.Matches(ordinal);
        }
    }
}
