using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;
using System.Buffers;

namespace SearchEngine.Query;

internal static class FacetFilterEvaluator
{
    internal static void Apply(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext qc)
    {
        if (filter.IsEmpty)
            return;

        ReadOnlySpan<FacetPredicate> predicates = filter.Predicates;
        if (predicates.Length == 0)
            return;

        int hitCount = results.GetTrueCount();
        if (hitCount == 0)
            return;

        if (SelectivityThresholds.UseFacetOnHitsPath(hitCount, snapshot.DocumentCount))
        {
            ApplyOnHitsInPlace(results, filter, snapshot, hitCount);
            return;
        }

        var resolved = ResolvePredicates(predicates, snapshot);
        var matching = qc.RentEmptyBitSet();

        for (int ordinal = 0; ordinal < matching.Length; ordinal++)
        {
            if (MatchesAll(resolved, ordinal))
                matching.Add(ordinal);
        }

        results.IntersectWith(matching);
    }

    private static void ApplyOnHitsInPlace(
        FastBitSet results,
        FacetFilter filter,
        IndexSnapshot snapshot,
        int hitCount)
    {
        int[] ordinals = ArrayPool<int>.Shared.Rent(hitCount);
        try
        {
            int count = results.CopySetBitOrdinals(ordinals.AsSpan(0, hitCount));
            var resolved = ResolvePredicates(filter.Predicates, snapshot);
            results.Clear();
            for (int i = 0; i < count; i++)
            {
                int ordinal = ordinals[i];
                if (MatchesAll(resolved, ordinal))
                    results.Add(ordinal);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    internal static FastBitSet BuildMatchingBitSet(
        FacetFilter filter,
        IndexSnapshot snapshot,
        QueryContext qc)
    {
        var matching = qc.RentEmptyBitSet();
        if (filter.IsEmpty)
        {
            matching.FillAllTrue();
            return matching;
        }

        var resolved = ResolvePredicates(filter.Predicates, snapshot);
        for (int ordinal = 0; ordinal < matching.Length; ordinal++)
        {
            if (MatchesAll(resolved, ordinal))
                matching.Add(ordinal);
        }

        return matching;
    }

    internal static int CountMatchingOrdinals(
        FacetFilter filter,
        IndexSnapshot snapshot,
        ReadOnlySpan<int> ordinals)
    {
        if (ordinals.Length == 0)
            return 0;

        if (filter.IsEmpty)
            return ordinals.Length;

        var resolved = ResolvePredicates(filter.Predicates, snapshot);
        int count = 0;
        for (int i = 0; i < ordinals.Length; i++)
        {
            if (MatchesAll(resolved, ordinals[i]))
                count++;
        }

        return count;
    }

    internal static List<int> MaterializeMatchingRecordIds(
        FacetFilter filter,
        IndexSnapshot snapshot,
        ReadOnlySpan<int> ordinals)
    {
        if (ordinals.Length == 0)
            return [];

        if (filter.IsEmpty)
            return PostingListOperations.MaterializeRecordIds(snapshot.RecordIds, ordinals);

        var resolved = ResolvePredicates(filter.Predicates, snapshot);
        var results = new List<int>(ordinals.Length);
        var recordIds = snapshot.RecordIds.AsSpan();
        for (int i = 0; i < ordinals.Length; i++)
        {
            int ordinal = ordinals[i];
            if (MatchesAll(resolved, ordinal))
                results.Add(recordIds[ordinal]);
        }

        return results;
    }

    internal static int CountFilterOnly(FacetFilter filter, IndexSnapshot snapshot)
    {
        if (filter.IsEmpty)
            return snapshot.DocumentCount;

        var resolved = ResolvePredicates(filter.Predicates, snapshot);
        int count = 0;
        for (int ordinal = 0; ordinal < snapshot.DocumentCount; ordinal++)
        {
            if (MatchesAll(resolved, ordinal))
                count++;
        }

        return count;
    }

    internal static List<int> MaterializeFilterOnly(
        FacetFilter filter,
        IndexSnapshot snapshot,
        SearchSortMode sortMode)
    {
        if (filter.IsEmpty)
            return [];

        var resolved = ResolvePredicates(filter.Predicates, snapshot);
        var results = new List<int>();
        var recordIds = snapshot.RecordIds.AsSpan();

        if (sortMode == SearchSortMode.NaturalSortAscending)
        {
            var permutation = snapshot.GetSortedPermutation();
            for (int i = 0; i < permutation.Length; i++)
            {
                int ordinal = permutation[i];
                if (MatchesAll(resolved, ordinal))
                    results.Add(recordIds[ordinal]);
            }
        }
        else
        {
            for (int ordinal = 0; ordinal < snapshot.DocumentCount; ordinal++)
            {
                if (MatchesAll(resolved, ordinal))
                    results.Add(recordIds[ordinal]);
            }
        }

        return results;
    }

    private static ResolvedPredicate[] ResolvePredicates(
        ReadOnlySpan<FacetPredicate> predicates,
        IndexSnapshot snapshot)
    {
        var resolved = new ResolvedPredicate[predicates.Length];
        for (int i = 0; i < predicates.Length; i++)
        {
            ref readonly FacetPredicate predicate = ref predicates[i];
            if (!snapshot.FacetColumns.TryGetValue(predicate.Facet, out long[]? column))
            {
                throw new ArgumentException($"Unknown facet '{predicate.Facet}'.");
            }

            resolved[i] = new ResolvedPredicate(predicate, column);
        }

        return resolved;
    }

    private static bool MatchesAll(ResolvedPredicate[] resolved, int ordinal)
    {
        for (int i = 0; i < resolved.Length; i++)
        {
            if (!Matches(resolved[i].Predicate, resolved[i].Column[ordinal]))
                return false;
        }

        return true;
    }

    private static bool Matches(in FacetPredicate predicate, long value)
    {
        return predicate.Kind switch
        {
            FacetPredicateKind.Range =>
                value >= predicate.MinInclusive && value <= predicate.MaxInclusive,
            FacetPredicateKind.Mask =>
                (value & predicate.MustHave) == predicate.MustHave
                && (value & predicate.MustNot) == 0,
            _ => false,
        };
    }

    private readonly record struct ResolvedPredicate(FacetPredicate Predicate, long[] Column);
}
