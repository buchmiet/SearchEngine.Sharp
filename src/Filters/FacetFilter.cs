namespace SearchEngine.Filters;

/// <summary>
/// Facet predicates joined with AND, applied after the text expression is evaluated (including <c>NOT</c>).
/// </summary>
/// <remarks>
/// <para>A document that was indexed without a value for a facet that other documents carry has the value 0 for
/// it, so a range that includes 0, or a mask whose <c>mustHave</c> is 0, matches such documents.</para>
/// <para>A query throws <see cref="ArgumentException"/> when a predicate names a facet that no document in the
/// snapshot carries, unless the index is empty or the text expression matches no documents. Names are not checked
/// when the filter is created.</para>
/// <para>There is no OR between predicates. Instances are immutable and safe to share between threads.</para>
/// </remarks>
public sealed class FacetFilter
{
    /// <summary>
    /// Gets the filter without predicates. A query that receives it behaves as if no filter were given.
    /// </summary>
    public static FacetFilter None { get; } = new([]);

    private readonly FacetPredicate[] _predicates;

    private FacetFilter(FacetPredicate[] predicates)
    {
        _predicates = predicates;
    }

    /// <summary>
    /// Gets whether this filter has no predicates.
    /// </summary>
    public bool IsEmpty => _predicates.Length == 0;

    internal ReadOnlySpan<FacetPredicate> Predicates => _predicates;

    /// <summary>
    /// Creates a filter with one range predicate: a document matches when its value of <paramref name="facet"/>
    /// lies between <paramref name="minInclusive"/> and <paramref name="maxInclusive"/>, both included.
    /// </summary>
    /// <param name="facet">The facet name (case-sensitive).</param>
    /// <param name="minInclusive">The smallest matching value.</param>
    /// <param name="maxInclusive">The largest matching value. When it is less than
    /// <paramref name="minInclusive"/>, no document matches.</param>
    /// <returns>A new filter. For equality pass the same value twice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facet"/> is <see langword="null"/>.</exception>
    public static FacetFilter Range(string facet, long minInclusive, long maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return new([new FacetRange(facet, minInclusive, maxInclusive)]);
    }

    /// <summary>
    /// Creates a filter with one bitmask predicate: a document matches when all bits of
    /// <paramref name="mustHave"/> are set in its value and no bit of <paramref name="mustNot"/> is.
    /// </summary>
    /// <param name="facet">The facet name (case-sensitive).</param>
    /// <param name="mustHave">The bits that must be set.</param>
    /// <param name="mustNot">The bits that must be clear.</param>
    /// <returns>A new filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facet"/> is <see langword="null"/>.</exception>
    public static FacetFilter Mask(string facet, long mustHave, long mustNot = 0)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return new([new FacetMask(facet, mustHave, mustNot)]);
    }

    /// <summary>
    /// Joins the predicates of all <paramref name="filters"/> with AND.
    /// </summary>
    /// <param name="filters">The filters to join.</param>
    /// <returns>A new filter with every predicate of <paramref name="filters"/>, or <see cref="None"/> when they
    /// have no predicates.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="filters"/> contains <see langword="null"/>.</exception>
    public static FacetFilter Combine(params FacetFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Length == 0)
            return None;

        int count = 0;
        foreach (FacetFilter filter in filters)
        {
            if (filter is null)
                throw new ArgumentException("The filters must not contain null.", nameof(filters));

            count += filter._predicates.Length;
        }

        if (count == 0)
            return None;

        var merged = new FacetPredicate[count];
        int offset = 0;
        foreach (FacetFilter filter in filters)
        {
            filter._predicates.CopyTo(merged, offset);
            offset += filter._predicates.Length;
        }

        return new(merged);
    }
}

// One condition on one facet column. The kinds are a closed set, so the evaluator's switch over them is exhaustive.
internal closed record class FacetPredicate(string Facet);

// minInclusive <= value <= maxInclusive.
internal sealed record class FacetRange(string Facet, long MinInclusive, long MaxInclusive) : FacetPredicate(Facet);

// Every bit of MustHave set and no bit of MustNot set.
internal sealed record class FacetMask(string Facet, long MustHave, long MustNot) : FacetPredicate(Facet);
