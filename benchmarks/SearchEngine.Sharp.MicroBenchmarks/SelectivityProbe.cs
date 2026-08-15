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

    internal static int NaturalSortKOnly(IndexSnapshot snapshot, FastBitSet resultSet, int[] ordinals, List<int> results)
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
        var resolved = FacetFilterEvaluatorProbe.ResolvePredicates(filter.Predicates, snapshot);
        for (int i = 0; i < hitCount; i++)
        {
            int ordinal = ordinals[i];
            if (FacetFilterEvaluatorProbe.MatchesAll(resolved, ordinal))
                matching.Add(ordinal);
        }

        results.IntersectWith(matching);
    }
}

/// <summary>Exposes FacetFilterEvaluator helpers to benchmarks without duplicating predicate logic.</summary>
internal static class FacetFilterEvaluatorProbe
{
    internal static ResolvedPredicate[] ResolvePredicates(ReadOnlySpan<FacetPredicate> predicates, IndexSnapshot snapshot)
    {
        var resolved = new ResolvedPredicate[predicates.Length];
        for (int i = 0; i < predicates.Length; i++)
        {
            ref readonly FacetPredicate predicate = ref predicates[i];
            if (!snapshot.FacetColumns.TryGetValue(predicate.Facet, out long[]? column))
                throw new ArgumentException($"Unknown facet '{predicate.Facet}'.");

            resolved[i] = new ResolvedPredicate(predicate, column);
        }

        return resolved;
    }

    internal static bool MatchesAll(ResolvedPredicate[] resolved, int ordinal)
    {
        for (int i = 0; i < resolved.Length; i++)
        {
            if (!Matches(resolved[i].Predicate, resolved[i].Column[ordinal]))
                return false;
        }

        return true;
    }

    private static bool Matches(in FacetPredicate predicate, long value)
        => predicate.Kind switch
        {
            FacetPredicateKind.Range =>
                value >= predicate.MinInclusive && value <= predicate.MaxInclusive,
            FacetPredicateKind.Mask =>
                (value & predicate.MustHave) == predicate.MustHave
                && (value & predicate.MustNot) == 0,
            _ => false,
        };

    internal readonly record struct ResolvedPredicate(FacetPredicate Predicate, long[] Column);
}
