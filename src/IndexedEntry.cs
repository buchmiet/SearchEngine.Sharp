namespace SearchEngine;

/// <summary>
/// One document to index: the text that queries match, the text that orders results, and optional facet values.
/// </summary>
/// <param name="SearchText">The text that is split into tokens and matched. The snapshot's
/// <see cref="SearchTokenization"/> decides how it is split and folded.</param>
/// <param name="SortText">The text that <see cref="SearchSortMode.NaturalSortAscending"/> orders by, usually the
/// original name. It is not tokenized, and the Unicode options do not change it.</param>
/// <param name="Facets">Optional named <see langword="long"/> values that a <see cref="Filters.FacetFilter"/> tests;
/// a missing value reads as 0.</param>
/// <exception cref="ArgumentNullException"><paramref name="SearchText"/> or <paramref name="SortText"/> is
/// <see langword="null"/>.</exception>
public sealed record IndexedEntry(
    string SearchText,
    string SortText,
    Filters.FacetValues? Facets = null)
{
    /// <summary>Gets the text that is split into tokens and matched.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public string SearchText
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(SearchText));
    } = SearchText ?? throw new ArgumentNullException(nameof(SearchText));

    /// <summary>Gets the text that <see cref="SearchSortMode.NaturalSortAscending"/> orders by.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public string SortText
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(SortText));
    } = SortText ?? throw new ArgumentNullException(nameof(SortText));

    // The entry of a string element of the collection argument paramName: the same text is searched and sorted.
    internal static IndexedEntry FromText(int id, string? text, string paramName)
        => text is null ? throw NullElement(id, paramName) : new IndexedEntry(text, text);

    // An IndexedEntry element of the collection argument paramName.
    internal static IndexedEntry Require(int id, IndexedEntry? entry, string paramName)
        => entry ?? throw NullElement(id, paramName);

    // A null element makes the collection argument invalid; it is not a null argument.
    internal static ArgumentException NullElement(int id, string paramName)
        => new($"The entry with id {id} is null.", paramName);
}
