namespace SearchEngine;

/// <summary>
/// Controls the order of search results.
/// </summary>
public enum SearchSortMode
{
    /// <summary>
    /// Results come in the order of the documents in the snapshot, which is the order in which the snapshot's
    /// entries were enumerated when it was built. An <see cref="IndexUpdater"/> keeps insertion order only until
    /// an entry is removed; after that, the order is not specified.
    /// </summary>
    SnapshotOrder = 0,

    /// <summary>
    /// Results are sorted in natural order of <see cref="IndexedEntry.SortText"/>, so <c>file2</c> comes before
    /// <c>file10</c>; see <see cref="Sorting.NaturalSortKeyBuilder"/> for the rules. The order of entries with equal
    /// keys is not specified.
    /// </summary>
    NaturalSortAscending = 1
}
