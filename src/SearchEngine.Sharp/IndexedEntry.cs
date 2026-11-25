namespace SearchEngine;

/// <summary>
/// One document to index: the text that queries match, the text that orders results, and optional facet values.
/// </summary>
/// <param name="SearchText">The text that is split into tokens and matched. The snapshot's
/// <see cref="SearchTokenization"/> decides how it is split and folded.</param>
/// <param name="SortText">The text that <see cref="SearchSortMode.NaturalSortAscending"/> orders by, usually the
/// original name. It is not tokenized, and the Unicode options do not change it.</param>
/// <param name="Facets">Optional named <see langword="long"/> values that a <see cref="Filters.FacetFilter"/> tests.
/// A document without a value for a facet that other documents carry has 0 in that facet.</param>
public sealed record IndexedEntry(
    string SearchText,
    string SortText,
    Filters.FacetValues? Facets = null);
