using System.Buffers;
using SearchEngine.Index;
using SearchEngine.Sorting;
using SearchEngine.Snapshots;

namespace SearchEngine.Query;

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

        var recordIds = snapshot.RecordIds.AsSpan();
        if (hitCount == 1)
        {
            Span<int> single = stackalloc int[1];
            resultSet.CopySetBitOrdinals(single);
            return [recordIds[single[0]]];
        }

        if (sortMode != SearchSortMode.NaturalSortAscending)
            return MaterializeSnapshotOrder(resultSet, hitCount, recordIds);

        return UsesSortKPath(snapshot, hitCount)
            ? MaterializeNaturalSortKOnly(snapshot, resultSet, hitCount, recordIds)
            : MaterializeNaturalSortFullPermutation(snapshot, resultSet, hitCount, recordIds);
    }

    // The natural sort strategy for hitCount hits: true sorts only the hits, false walks the sort permutation.
    // The permutation's state is read once, so a build by another thread in between only changes the cost.
    internal static bool UsesSortKPath(IndexSnapshot snapshot, int hitCount)
        => SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount, snapshot.IsSortedPermutationBuilt);

    private static List<int> MaterializeSnapshotOrder(
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            return PostingListOperations.MaterializeRecordIds(recordIds, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    // Walks the sort permutation of the whole snapshot and keeps the hits: cost grows with the document count.
    internal static List<int> MaterializeNaturalSortFullPermutation(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        var results = new List<int>(hitCount);
        foreach (int ordinal in snapshot.GetSortedPermutation())
        {
            if (resultSet.Get(ordinal))
                results.Add(recordIds[ordinal]);
        }

        return results;
    }

    // Builds a sort key for each hit and sorts only the hits: cost grows with the hit count.
    internal static List<int> MaterializeNaturalSortKOnly(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            NaturalOrder.Sort(ordinals.AsSpan(0, count), snapshot.SortTextArray, snapshot.RecordIds);
            return PostingListOperations.MaterializeRecordIds(recordIds, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
