using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;

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

        var resolved = ResolvePredicates(predicates, snapshot);
        var matching = qc.RentEmptyBitSet();

        for (int ordinal = 0; ordinal < matching.Length; ordinal++)
        {
            if (MatchesAll(resolved, ordinal))
                matching.Add(ordinal);
        }

        results.IntersectWith(matching);
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
