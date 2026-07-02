namespace SearchEngine.Filters;

/// <summary>
/// Inclusive numeric range predicate on a facet column.
/// </summary>
public readonly record struct FacetRange(string Facet, long MinInclusive, long MaxInclusive);

/// <summary>
/// Bitmask predicate on a facet column:
/// <c>(value &amp; MustHave) == MustHave &amp;&amp; (value &amp; MustNot) == 0</c>.
/// </summary>
public readonly record struct FacetMask(string Facet, long MustHave, long MustNot = 0);
