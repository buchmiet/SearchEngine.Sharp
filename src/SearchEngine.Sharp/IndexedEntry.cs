namespace SearchEngine;

/// <summary>
/// Represents an entry to be indexed, separating search text from sort text.
/// </summary>
/// <param name="SearchText">Material for tokenization and matching.</param>
/// <param name="SortText">Original name used to build a natural sort key.</param>
public sealed record IndexedEntry(
    string SearchText,
    string SortText);
