namespace SearchEngine;

/// <summary>
/// Controls the order of search results.
/// </summary>
public enum SearchSortMode
{
    /// <summary>
    /// Results come in the order of the documents in the snapshot. A snapshot built by
    /// <see cref="Snapshots.IndexSnapshotBuilder"/> keeps the order in which its entries were enumerated. An
    /// <see cref="IndexUpdater"/> keeps the order of <see cref="IIndexUpdater.RebuildFrom(IDictionary{int, string})"/>
    /// followed by the order in which entries were added, but only while no entry is removed or replaced; after that,
    /// until the next <see cref="IIndexUpdater.RebuildFrom(IDictionary{int, string})"/> or
    /// <see cref="IIndexUpdater.Clear"/>, the order is not specified and can change with every later change.
    /// </summary>
    SnapshotOrder = 0,

    /// <summary>
    /// Results are sorted in natural order of <see cref="IndexedEntry.SortText"/>, so <c>file2</c> comes before
    /// <c>file10</c>; see <see cref="Sorting.NaturalSortKeyBuilder"/> for the rules. Entries whose sort texts compare
    /// equal come in ascending id order, so the same query on the same entries always returns the same order.
    /// </summary>
    NaturalSortAscending = 1
}
