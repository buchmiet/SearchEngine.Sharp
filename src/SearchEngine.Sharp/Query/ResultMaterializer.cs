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

        return sortMode == SearchSortMode.NaturalSortAscending
            ? MaterializeNaturalSort(snapshot, resultSet, hitCount, recordIds)
            : MaterializeSnapshotOrder(resultSet, hitCount, recordIds);
    }

    private static List<int> MaterializeSnapshotOrder(
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        if (hitCount == 1)
        {
            Span<int> single = stackalloc int[1];
            resultSet.CopySetBitOrdinals(single);
            return [recordIds[single[0]]];
        }

        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = resultSet.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
                results.Add(recordIds[ordinals[i]]);

            return results;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    private static List<int> MaterializeNaturalSort(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        if (hitCount == 1)
        {
            Span<int> single = stackalloc int[1];
            resultSet.CopySetBitOrdinals(single);
            return [recordIds[single[0]]];
        }

        if (SelectivityThresholds.UseSortKPath(hitCount, snapshot.DocumentCount))
            return MaterializeNaturalSortKOnly(snapshot, resultSet, hitCount, recordIds);

        return MaterializeNaturalSortFullPermutation(snapshot, resultSet, hitCount, recordIds);
    }

    // Walks the sort permutation of the whole snapshot and keeps the hits: cost grows with the document count.
    internal static List<int> MaterializeNaturalSortFullPermutation(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int hitCount,
        ReadOnlySpan<int> recordIds)
    {
        var results = new List<int>(hitCount);
        var permutation = snapshot.GetSortedPermutation();
        foreach (int ordinal in permutation)
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

            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
                results.Add(recordIds[ordinals[i]]);

            return results;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }
}
