using System.Buffers;
using SearchEngine.Index;
using SearchEngine.Sorting;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

// Turns the bitset of a query, without dead ordinals (IndexSnapshot.RemoveDeadOrdinals), into the caller's ids.
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

    // The natural sort strategy for hitCount hits: true sorts only the hits, false walks the sort permutation.
    // The permutation's state is read once, so a build by another thread in between only changes the cost.
    internal static bool UsesSortKPath(IndexSnapshot snapshot, int hitCount)
        => SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount, snapshot.Base.IsSortedPermutationBuilt);

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

    // Walks the sort permutation of the base and keeps the hits, placing each delta document before the base
    // document of its rank (DeltaNaturalOrder): cost grows with the document count.
    internal static List<int> MaterializeNaturalSortFullPermutation(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount)
    {
        var results = new List<int>(hitCount);
        var baseIds = snapshot.Base.RecordIds;
        var permutation = snapshot.Base.GetSortedPermutation();
        var delta = snapshot.DeltaOrder;
        if (delta is null)
        {
            foreach (int ordinal in permutation)
            {
                if (resultSet.Get(ordinal))
                    results.Add(baseIds[ordinal]);
            }

            return results;
        }

        var deltaOrdinals = delta.Ordinals;
        var baseRanks = delta.BaseRanks;
        var deltaIds = snapshot.Delta!.RecordIds;
        int deltaStart = snapshot.Delta.FirstOrdinal;
        int next = 0;
        for (int rank = 0; rank <= permutation.Length; rank++)
        {
            for (; next < deltaOrdinals.Length && baseRanks[next] == rank; next++)
            {
                if (resultSet.Get(deltaOrdinals[next]))
                    results.Add(deltaIds[deltaOrdinals[next] - deltaStart]);
            }

            if (rank < permutation.Length && resultSet.Get(permutation[rank]))
                results.Add(baseIds[permutation[rank]]);
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
            NaturalOrder.Sort(ordinals.AsSpan(0, count), snapshot.Base, snapshot.Delta);
            return PostingListOperations.MaterializeRecordIds(snapshot, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
