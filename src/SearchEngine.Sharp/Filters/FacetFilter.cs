namespace SearchEngine.Filters;

/// <summary>
/// AND-combined facet predicates applied after the text expression is evaluated.
/// </summary>
public sealed class FacetFilter
{
    /// <summary>
    /// Filter that matches all documents (no facet constraints).
    /// </summary>
    public static FacetFilter None { get; } = new([]);

    private readonly FacetPredicate[] _predicates;

    private FacetFilter(FacetPredicate[] predicates)
    {
        _predicates = predicates;
    }

    /// <summary>
    /// Gets whether this filter contains any predicates.
    /// </summary>
    public bool IsEmpty => _predicates.Length == 0;

    internal ReadOnlySpan<FacetPredicate> Predicates => _predicates;

    /// <summary>
    /// Creates a filter with one inclusive range predicate.
    /// </summary>
    public static FacetFilter Range(string facet, long minInclusive, long maxInclusive)
        => new([new FacetRange(facet, minInclusive, maxInclusive)]);

    /// <summary>
    /// Creates a filter with one bitmask predicate.
    /// </summary>
    public static FacetFilter Mask(string facet, long mustHave, long mustNot = 0)
        => new([new FacetMask(facet, mustHave, mustNot)]);

    /// <summary>
    /// Combines multiple filters with logical AND.
    /// </summary>
    public static FacetFilter Combine(params FacetFilter[] filters)
    {
        if (filters.Length == 0)
            return None;

        int count = 0;
        foreach (FacetFilter filter in filters)
            count += filter._predicates.Length;

        if (count == 0)
            return None;

        var merged = new FacetPredicate[count];
        int offset = 0;
        foreach (FacetFilter filter in filters)
        {
            filter._predicates.CopyTo(merged, offset);
            offset += filter._predicates.Length;
        }

        return new FacetFilter(merged);
    }
}

// One condition on one facet column. The kinds are a closed set, so the evaluator's switch over them is exhaustive.
internal closed record class FacetPredicate(string Facet);

// minInclusive <= value <= maxInclusive.
internal sealed record class FacetRange(string Facet, long MinInclusive, long MaxInclusive) : FacetPredicate(Facet);

// Every bit of MustHave set and no bit of MustNot set.
internal sealed record class FacetMask(string Facet, long MustHave, long MustNot) : FacetPredicate(Facet);
