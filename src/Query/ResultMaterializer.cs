using System.Buffers;
using SearchEngine.Index;
using SearchEngine.Sorting;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Turns the bitset of a query, without dead ordinals (IndexSnapshot.RemoveDeadOrdinals), into record ids.
internal static class ResultMaterializer
{
    internal static List<int> Materialize(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        SearchSortMode sortMode)
    {
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
            return MaterializeSnapshotOrder(snapshot, resultSet, hitCount);

        return UsesSortKPath(snapshot, hitCount)
            ? MaterializeNaturalSortKOnly(snapshot, resultSet, hitCount)
            : MaterializeNaturalSortFullPermutation(snapshot, resultSet, hitCount);
    }

    // The natural sort strategy for hitCount hits: true sorts the hits, false walks the permutation. The state of the
    // permutation of the largest segment is read once, so a build by another thread in between only changes the cost.
    // The natural order of the other segments is not counted; it is built once per snapshot, on first use
    // (IndexSnapshot.OtherOrder).
    internal static bool UsesSortKPath(IndexSnapshot snapshot, int hitCount)
        => SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount, snapshot.Primary.IsSortedPermutationBuilt);

    private static List<int> MaterializeSnapshotOrder(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    // Walks the sort permutation of the largest segment and keeps the hits, placing each document of the other segments
    // before the document of its rank (OtherNaturalOrder): cost grows with the document count.
    internal static List<int> MaterializeNaturalSortFullPermutation(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount)
    {
        var results = new List<int>(hitCount);
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
                    results.Add(primaryIds[local]);
            }

            return results;
        }

        var otherOrdinals = other.Ordinals;
        var primaryRanks = other.PrimaryRanks;
        int next = 0;
        for (int rank = 0; rank <= permutation.Length; rank++)
        {
            for (; next < otherOrdinals.Length && primaryRanks[next] == rank; next++)
            {
                if (resultSet.Get(otherOrdinals[next]))
                    results.Add(snapshot.RecordIdAt(otherOrdinals[next]));
            }

            if (rank < permutation.Length && resultSet.Get(primaryOffset + permutation[rank]))
                results.Add(primaryIds[permutation[rank]]);
        }

        return results;
    }

    // Builds a sort key for each hit and sorts only the hits: cost grows with the hit count.
    internal static List<int> MaterializeNaturalSortKOnly(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            NaturalOrder.Sort(ordinals.AsSpan(0, count), snapshot);
            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
