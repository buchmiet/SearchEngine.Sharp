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

        return SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount)
            ? MaterializeNaturalSortKOnly(snapshot, resultSet, hitCount, recordIds)
            : MaterializeNaturalSortByPermutation(snapshot, resultSet, hitCount, recordIds);
    }

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

    // Dense results: walk the snapshot's natural-sort permutation and keep the hits.
    private static List<int> MaterializeNaturalSortByPermutation(
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

    // Sparse results: sort only the hits by their natural sort keys.
    private static List<int> MaterializeNaturalSortKOnly(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        string[] keys = ArrayPool<string>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            string[] sortTexts = snapshot.SortTextArray;
            for (int i = 0; i < count; i++)
                keys[i] = NaturalSortKeyBuilder.Build(sortTexts[ordinals[i]]);

            Array.Sort(keys, ordinals, 0, count, StringComparer.Ordinal);
            return PostingListOperations.MaterializeRecordIds(recordIds, ordinals.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<string>.Shared.Return(keys, clearArray: true);
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
