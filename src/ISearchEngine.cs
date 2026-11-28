namespace SearchEngine;

/// <summary>
/// Runs queries against the current snapshot of an <see cref="IIndexSnapshotProvider"/>.
/// </summary>
/// <remarks>
/// <para>Each call reads <see cref="IIndexSnapshotProvider.Current"/> once, so it sees the index either before or
/// after a publish, never a mix. Implementations keep no state of their own and are safe to call from many threads
/// at once.</para>
/// <para>An expression is split into terms at the query separators of the snapshot's
/// <see cref="SearchTokenization"/>. Adjacent terms are joined with AND. A term that contains <c>*</c> or <c>?</c>
/// is a glob that must match a whole token. With <c>enableOperators</c> set, the words <c>and</c>, <c>or</c> and
/// <c>not</c> (ASCII case-insensitive) are operators, and so are parentheses when both are query separators and
/// they balance; a malformed expression is corrected, never rejected. Under <see cref="WordMatchMethod.Regex"/> the
/// whole expression is one pattern and <c>enableOperators</c> is ignored.</para>
/// <para>An empty or whitespace expression matches nothing, unless a non-empty facet filter is given; then every
/// document that passes the filter matches. A <see langword="null"/> expression throws
/// <see cref="ArgumentNullException"/>.</para>
/// <para>The first <see cref="WordMatchMethod.Within"/> query and the first
/// <see cref="SearchSortMode.NaturalSortAscending"/> query that need a lookup structure build it (see
/// <see cref="Snapshots.IndexSnapshot"/>), so they take longer than later ones.</para>
/// </remarks>
public interface ISearchEngine
{
    /// <summary>
    /// Returns the ids of the documents that match <paramref name="expression"/>, in snapshot order.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="method">How terms match indexed tokens.</param>
    /// <param name="enableOperators"><see langword="true"/> to recognize the operators described in the remarks of
    /// <see cref="ISearchEngine"/>. Ignored for <see cref="WordMatchMethod.Regex"/>.</param>
    /// <returns>A new list of the ids given to the updater or builder. It is empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false);

    /// <summary>
    /// Returns the ids of the documents that match <paramref name="expression"/>, in the order given by
    /// <paramref name="sortMode"/>.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="method">How terms match indexed tokens.</param>
    /// <param name="enableOperators"><see langword="true"/> to recognize the operators described in the remarks of
    /// <see cref="ISearchEngine"/>. Ignored for <see cref="WordMatchMethod.Regex"/>.</param>
    /// <param name="sortMode">The order of the returned ids.</param>
    /// <returns>A new list of the ids given to the updater or builder. It is empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode);

    /// <summary>
    /// Returns the ids of the documents that match <paramref name="expression"/> and pass
    /// <paramref name="filter"/>, in the order given by <paramref name="sortMode"/>.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="method">How terms match indexed tokens.</param>
    /// <param name="enableOperators"><see langword="true"/> to recognize the operators described in the remarks of
    /// <see cref="ISearchEngine"/>. Ignored for <see cref="WordMatchMethod.Regex"/>.</param>
    /// <param name="sortMode">The order of the returned ids.</param>
    /// <param name="filter">Facet predicates applied after the expression, or <see langword="null"/> for none.</param>
    /// <returns>A new list of the ids given to the updater or builder. It is empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    List<int> Find(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        SearchSortMode sortMode,
        Filters.FacetFilter? filter);

    /// <summary>
    /// Returns the number of documents that match <paramref name="expression"/> without building the list of ids.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="method">How terms match indexed tokens.</param>
    /// <param name="enableOperators"><see langword="true"/> to recognize the operators described in the remarks of
    /// <see cref="ISearchEngine"/>. Ignored for <see cref="WordMatchMethod.Regex"/>.</param>
    /// <returns>The number of ids that <see cref="Find(string, WordMatchMethod, bool)"/> would return.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false);

    /// <summary>
    /// Returns the number of documents that match <paramref name="expression"/> and pass <paramref name="filter"/>
    /// without building the list of ids.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="method">How terms match indexed tokens.</param>
    /// <param name="enableOperators"><see langword="true"/> to recognize the operators described in the remarks of
    /// <see cref="ISearchEngine"/>. Ignored for <see cref="WordMatchMethod.Regex"/>.</param>
    /// <param name="filter">Facet predicates applied after the expression, or <see langword="null"/> for none.</param>
    /// <returns>The number of ids that
    /// <see cref="Find(string, WordMatchMethod, bool, SearchSortMode, Filters.FacetFilter?)"/> would return.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    int CountMatches(string expression, WordMatchMethod method, bool enableOperators, Filters.FacetFilter? filter);

    /// <summary>
    /// Gets the number of documents in the current snapshot.
    /// </summary>
    int DocumentCount { get; }

    /// <summary>
    /// Gets the number of distinct tokens in the current snapshot.
    /// </summary>
    int UniqueWordCount { get; }
}
