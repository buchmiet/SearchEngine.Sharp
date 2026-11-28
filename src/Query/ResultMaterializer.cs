using System.Buffers;
using SearchEngine.Index;
using SearchEngine.Sorting;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Turns the bitset of a query, without dead ordinals (IndexSnapshot.RemoveDeadOrdinals), into record ids.
internal static class ResultMaterializer
{
    // The ids of the first limit hits in the order of sortMode; limit is at least 1.
    internal static List<int> Materialize(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        SearchSortMode sortMode,
        int limit = int.MaxValue)
    {
        // A small limit in snapshot order needs neither the hit count nor a rented buffer.
        if (sortMode != SearchSortMode.NaturalSortAscending && limit <= StackLimit)
        {
            Span<int> first = stackalloc int[limit];
            int found = resultSet.CopyFirstSetBitOrdinals(first);
            return PostingListOperations.MaterializeRecordIds(snapshot, first[..found]);
        }

        int hitCount = resultSet.GetTrueCount();
        if (hitCount == 0)
            return [];

        if (hitCount == 1)
        {
            Span<int> single = stackalloc int[1];
            resultSet.CopySetBitOrdinals(single);
            return [snapshot.RecordIdAt(single[0])];
        }

        if (sortMode != SearchSortMode.NaturalSortAscending)
            return MaterializeSnapshotOrder(snapshot, resultSet, Math.Min(hitCount, limit));

        return UsesSortKPath(snapshot, hitCount, limit)
            ? MaterializeNaturalSortKOnly(snapshot, resultSet, hitCount, limit)
            : MaterializeNaturalSortFullPermutation(snapshot, resultSet, hitCount, limit);
    }

    // The largest limit whose ordinals are copied to the stack.
    private const int StackLimit = 256;

    // The natural sort strategy for hitCount hits of which the first limit are wanted: true sorts the hits, false walks
    // the permutation. The state of the permutation of the largest segment is read once, so a build by another thread
    // in between only changes the cost. The natural order of the other segments is not counted; it is built once per
    // snapshot, on first use (IndexSnapshot.OtherOrder).
    internal static bool UsesSortKPath(IndexSnapshot snapshot, int hitCount, int limit = int.MaxValue)
        => SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount, snapshot.Primary.IsSortedPermutationBuilt, limit);

    private static List<int> MaterializeSnapshotOrder(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int take)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(take);
        try
        {
            int count = resultSet.CopyFirstSetBitOrdinals(ordinals.AsSpan(0, take));
            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    // Walks the sort permutation of the largest segment and keeps the hits until limit are found, placing each document
    // of the other segments before the document of its rank (OtherNaturalOrder): cost grows with the position in the
    // permutation of the last hit taken.
    internal static List<int> MaterializeNaturalSortFullPermutation(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        int limit = int.MaxValue)
    {
        limit = Math.Min(limit, hitCount);
        var results = new List<int>(limit);
        var primary = snapshot.Primary;
        var primaryIds = primary.RecordIds;
        int primaryOffset = snapshot.Offsets[snapshot.PrimaryIndex];
        var permutation = primary.GetSortedPermutation();
        var other = snapshot.OtherOrder;
        if (other is null)
        {
            foreach (int local in permutation)
            {
                if (resultSet.Get(primaryOffset + local))
                {
                    results.Add(primaryIds[local]);
                    if (results.Count == limit)
                        break;
                }
            }

            return results;
        }

        var otherOrdinals = other.Ordinals;
        var primaryRanks = other.PrimaryRanks;
        int next = 0;
        for (int rank = 0; rank <= permutation.Length && results.Count < limit; rank++)
        {
            for (; next < otherOrdinals.Length && primaryRanks[next] == rank; next++)
            {
                if (resultSet.Get(otherOrdinals[next]))
                {
                    results.Add(snapshot.RecordIdAt(otherOrdinals[next]));
                    if (results.Count == limit)
                        return results;
                }
            }

            if (rank < permutation.Length && resultSet.Get(primaryOffset + permutation[rank]))
                results.Add(primaryIds[permutation[rank]]);
        }

        return results;
    }

    // Builds a sort key for each hit and sorts only the hits: cost grows with the hit count. With a limit below the
    // hit count it keeps only the first limit hits (NaturalOrder.SelectFirst).
    internal static List<int> MaterializeNaturalSortKOnly(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        int limit = int.MaxValue)
    {
        int take = Math.Min(hitCount, limit);
        int[] ordinals = ArrayPool<int>.Shared.Rent(take);
        try
        {
            if (take < hitCount)
            {
                NaturalOrder.SelectFirst(resultSet, snapshot, ordinals.AsSpan(0, take));
            }
            else
            {
                resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, take));
                NaturalOrder.Sort(ordinals.AsSpan(0, take), snapshot);
            }

            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals.AsSpan(0, take));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
