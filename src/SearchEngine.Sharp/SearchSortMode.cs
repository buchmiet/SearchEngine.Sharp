namespace SearchEngine;

/// <summary>
/// Controls the order of search results.
/// </summary>
public enum SearchSortMode
{
    /// <summary>
    /// Results are returned in snapshot (insertion) order.
    /// </summary>
    SnapshotOrder = 0,

    /// <summary>
    /// Results are sorted by natural sort key ascending.
    /// </summary>
    NaturalSortAscending = 1
}
