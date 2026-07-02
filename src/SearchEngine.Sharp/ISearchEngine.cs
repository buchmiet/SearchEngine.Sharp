namespace SearchEngine;

/// <summary>
/// Executes search queries against the current index snapshot.
/// Stateless: all state comes from IIndexSnapshotProvider.
/// </summary>
public interface ISearchEngine
{
    /// <summary>
    /// Searches for entries matching the given expression.
    /// </summary>
    /// <param name="expression">Search expression.</param>
    /// <param name="method">Matching method (Exact or Within).</param>
    /// <param name="enableOperators">Enable AND/OR/NOT operators in the expression.</param>
    /// <returns>List of matching document IDs.</returns>
    List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false);

    /// <summary>
    /// Searches for entries matching the given expression with sort control.
    /// </summary>
    List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode);

    /// <summary>
    /// Searches for entries matching the given expression with optional facet filtering.
    /// </summary>
    List<int> Find(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        SearchSortMode sortMode,
        Filters.FacetFilter? filter);

    /// <summary>
    /// Counts entries matching the given expression without materializing document IDs.
    /// </summary>
    int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false);

    /// <summary>
    /// Counts entries matching the given expression with optional facet filtering.
    /// </summary>
    int CountMatches(string expression, WordMatchMethod method, bool enableOperators, Filters.FacetFilter? filter);

    /// <summary>
    /// Gets the number of indexed documents in the current snapshot.
    /// </summary>
    int DocumentCount { get; }

    /// <summary>
    /// Gets the number of unique words in the current snapshot.
    /// </summary>
    int UniqueWordCount { get; }
}
