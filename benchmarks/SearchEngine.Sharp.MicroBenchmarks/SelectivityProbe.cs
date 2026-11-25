using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Sorting;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Benchmark-only helpers comparing current O(N) pipeline stages with selectivity-aware prototypes.
/// </summary>
internal static class SelectivityProbe
{
    internal static FastBitSet CreateSparseBitSet(int documentCount, int hitCount)
    {
        var bitSet = new FastBitSet(documentCount);
        if (hitCount <= 0)
            return bitSet;

        int k = Math.Min(hitCount, documentCount);
        if (k >= documentCount)
        {
            bitSet.FillAllTrue();
            return bitSet;
        }

        // Spread hits across the index to avoid artificially dense low-ordinal clustering.
        int stride = Math.Max(1, documentCount / k);
        for (int i = 0, added = 0; added < k; i += stride, added++)
            bitSet.Add(i);

        return bitSet;
    }

    internal static int MaterializeSnapshotOrder(IndexSnapshot snapshot, FastBitSet resultSet, List<int> results)
    {
        results.Clear();
        var recordIds = snapshot.RecordIds.AsSpan();
        for (int ordinal = 0; ordinal < resultSet.Length; ordinal++)
        {
            if (resultSet.Get(ordinal))
                results.Add(recordIds[ordinal]);
        }

        return results.Count;
    }

    internal static int MaterializeEnumerate(IndexSnapshot snapshot, FastBitSet resultSet, Span<int> ordinals, List<int> results)
    {
        results.Clear();
        int hitCount = resultSet.CopySetBitOrdinals(ordinals);
        var recordIds = snapshot.RecordIds.AsSpan();
        for (int i = 0; i < hitCount; i++)
            results.Add(recordIds[ordinals[i]]);

        return results.Count;
    }

    internal static int NaturalSortCurrent(IndexSnapshot snapshot, FastBitSet resultSet, List<int> results)
    {
        results.Clear();
        var permutation = snapshot.GetSortedPermutation();
        var recordIds = snapshot.RecordIds.AsSpan();
        foreach (int ordinal in permutation)
        {
            if (resultSet.Get(ordinal))
                results.Add(recordIds[ordinal]);
        }

        return results.Count;
    }

    internal static int NaturalSortKOnlyNaive(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int[] ordinals,
        List<int> results)
    {
        results.Clear();
        int hitCount = resultSet.CopySetBitOrdinals(ordinals.AsSpan());
        if (hitCount == 0)
            return 0;

        if (hitCount == 1)
        {
            results.Add(snapshot.RecordIds[ordinals[0]]);
            return 1;
        }

        string[] sortTexts = snapshot.SortTextArray;
        var recordIds = snapshot.RecordIds;
        Array.Sort(ordinals, 0, hitCount, Comparer<int>.Create((a, b) =>
        {
            int cmp = string.Compare(
                NaturalSortKeyBuilder.Build(sortTexts[a]),
                NaturalSortKeyBuilder.Build(sortTexts[b]),
                StringComparison.Ordinal);
            return cmp != 0 ? cmp : recordIds[a].CompareTo(recordIds[b]);
        }));

        for (int i = 0; i < hitCount; i++)
            results.Add(recordIds[ordinals[i]]);

        return results.Count;
    }

    /// <summary>
    /// Production-shaped prototype: build each natural sort key exactly once, then sort K ordinals by cached keys.
    /// </summary>
    internal static int NaturalSortKOnlyPrecomputedKeys(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        int[] ordinals,
        string[] keyScratch,
        List<int> results)
    {
        results.Clear();
        int hitCount = resultSet.CopySetBitOrdinals(ordinals.AsSpan());
        if (hitCount == 0)
            return 0;

        if (hitCount == 1)
        {
            results.Add(snapshot.RecordIds[ordinals[0]]);
            return 1;
        }

        string[] sortTexts = snapshot.SortTextArray;
        var recordIds = snapshot.RecordIds;
        for (int i = 0; i < hitCount; i++)
            keyScratch[i] = NaturalSortKeyBuilder.Build(sortTexts[ordinals[i]]);

        Array.Sort(keyScratch, ordinals, 0, hitCount, StringComparer.Ordinal);

        for (int i = 0; i < hitCount; i++)
            results.Add(recordIds[ordinals[i]]);

        return results.Count;
    }

    internal static void FacetApplyFullScan(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext queryContext)
        => FacetFilterEvaluator.Apply(results, filter, snapshot, queryContext);

    internal static void FacetApplyOnHitsOnly(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext queryContext,
        Span<int> ordinals)
    {
        if (filter.IsEmpty)
            return;

        int hitCount = results.CopySetBitOrdinals(ordinals);
        if (hitCount == 0)
        {
            results.Clear();
            return;
        }

        var matching = queryContext.RentEmptyBitSet();
        var resolved = FacetFilterEvaluator.ResolvePredicates(filter.Predicates, snapshot);
        for (int i = 0; i < hitCount; i++)
        {
            int ordinal = ordinals[i];
            if (FacetFilterEvaluator.MatchesAll(resolved, ordinal))
                matching.Add(ordinal);
        }

        results.IntersectWith(matching);
    }

    /// <summary>
    /// Tighter prototype: enumerate text hits, clear bitset, re-add only facet matches (no second bitset + intersect).
    /// </summary>
    internal static void FacetApplyOnHitsInPlace(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        Span<int> ordinals)
    {
        if (filter.IsEmpty)
            return;

        int hitCount = results.CopySetBitOrdinals(ordinals);
        if (hitCount == 0)
        {
            results.Clear();
            return;
        }

        var resolved = FacetFilterEvaluator.ResolvePredicates(filter.Predicates, snapshot);
        results.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            int ordinal = ordinals[i];
            if (FacetFilterEvaluator.MatchesAll(resolved, ordinal))
                results.Add(ordinal);
        }
    }
}
