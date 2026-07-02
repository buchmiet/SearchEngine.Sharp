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
        => new([FacetPredicate.Range(facet, minInclusive, maxInclusive)]);

    /// <summary>
    /// Creates a filter with one bitmask predicate.
    /// </summary>
    public static FacetFilter Mask(string facet, long mustHave, long mustNot = 0)
        => new([FacetPredicate.Mask(facet, mustHave, mustNot)]);

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

internal readonly struct FacetPredicate
{
    internal FacetPredicateKind Kind { get; init; }
    internal string Facet { get; init; }
    internal long MinInclusive { get; init; }
    internal long MaxInclusive { get; init; }
    internal long MustHave { get; init; }
    internal long MustNot { get; init; }

    internal static FacetPredicate Range(string facet, long minInclusive, long maxInclusive)
        => new()
        {
            Kind = FacetPredicateKind.Range,
            Facet = facet,
            MinInclusive = minInclusive,
            MaxInclusive = maxInclusive,
        };

    internal static FacetPredicate Mask(string facet, long mustHave, long mustNot)
        => new()
        {
            Kind = FacetPredicateKind.Mask,
            Facet = facet,
            MustHave = mustHave,
            MustNot = mustNot,
        };
}

internal enum FacetPredicateKind
{
    Range,
    Mask,
}
