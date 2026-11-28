using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Runs queries against the current snapshot of an <see cref="IIndexSnapshotProvider"/>; see
/// <see cref="ISearchEngine"/> for the query rules and thread safety.
/// </summary>
/// <param name="snapshotProvider">The provider whose <see cref="IIndexSnapshotProvider.Current"/> snapshot each
/// query reads.</param>
/// <exception cref="ArgumentNullException"><paramref name="snapshotProvider"/> is <see langword="null"/>.</exception>
public sealed class SearchEngineSharp(IIndexSnapshotProvider snapshotProvider) : ISearchEngine
{
    private readonly IIndexSnapshotProvider _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));

    /// <inheritdoc />
    public int DocumentCount => _snapshotProvider.Current.DocumentCount;

    /// <inheritdoc />
    public int UniqueWordCount => _snapshotProvider.Current.UniqueWordCount;

    /// <inheritdoc />
    public List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false)
    {
        return Find(expression, method, enableOperators, SearchSortMode.SnapshotOrder);
    }

    /// <inheritdoc />
    public int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false)
        => CountMatches(expression, method, enableOperators, null);

    /// <inheritdoc />
    public int CountMatches(string expression, WordMatchMethod method, bool enableOperators, FacetFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var snapshot = _snapshotProvider.Current;
        if (!HasQueryInput(expression, filter) || snapshot.DocumentCount == 0)
            return 0;

        if (TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
            return filter is null ? exactMatches.Count : FacetFilterEvaluator.CountMatchingOrdinals(filter, snapshot, exactMatches);

        using var queryContext = new QueryContext(snapshot.OrdinalCount);
        return Evaluate(snapshot, expression, method, enableOperators, filter, queryContext)?.GetTrueCount() ?? 0;
    }

    /// <inheritdoc />
    public List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode)
        => Find(expression, method, enableOperators, sortMode, null);

    /// <inheritdoc />
    public List<int> Find(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        SearchSortMode sortMode,
        FacetFilter? filter)
        => Find(expression, method, enableOperators, sortMode, filter, int.MaxValue);

    /// <inheritdoc />
    public List<int> Find(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        SearchSortMode sortMode,
        FacetFilter? filter,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentOutOfRangeException.ThrowIfNegative(maxResults);
        var snapshot = _snapshotProvider.Current;
        if (maxResults == 0 || !HasQueryInput(expression, filter) || snapshot.DocumentCount == 0)
            return [];

        if (sortMode == SearchSortMode.SnapshotOrder
            && TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
        {
            return filter is null
                ? PostingListOperations.MaterializeRecordIds(snapshot, exactMatches, maxResults)
                : FacetFilterEvaluator.MaterializeMatchingRecordIds(filter, snapshot, exactMatches, maxResults);
        }

        using var queryContext = new QueryContext(snapshot.OrdinalCount);
        var resultSet = Evaluate(snapshot, expression, method, enableOperators, filter, queryContext);
        return resultSet is null ? [] : ResultMaterializer.Materialize(snapshot, resultSet, sortMode, maxResults);
    }

    // True when the query selects anything: a non-blank expression or a non-empty filter.
    private static bool HasQueryInput(string expression, FacetFilter? filter)
        => !string.IsNullOrWhiteSpace(expression) || filter is not null && !filter.IsEmpty;

    // Fast path of an Exact query for one plain term (no glob metacharacters): its live postings are read straight
    // from the snapshot, without a bitset. False when the query is not such a term; empty postings when the term is
    // not an indexed token.
    private static bool TryGetExactWordPostings(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        IndexSnapshot snapshot,
        out LivePostings postings)
    {
        postings = default;
        if (method != WordMatchMethod.Exact
            || !QueryExpressionEvaluator.TryGetSingleSemanticWord(
                expression.AsSpan(),
                enableOperators,
                snapshot.Tokenization,
                out var word)
            || GlobMatcher.ContainsMetacharacters(word!))
            return false;

        postings = snapshot.PostingsOf(word!);
        return true;
    }

    // The ordinals of the live documents that match the expression and the filter; null when the query selects
    // nothing. A blank expression with a filter selects every document the filter matches.
    private static FastBitSet? Evaluate(
        IndexSnapshot snapshot,
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        FacetFilter? filter,
        QueryContext queryContext)
    {
        bool hasFilter = filter is not null && !filter.IsEmpty;
        FastBitSet? resultSet;
        if (string.IsNullOrWhiteSpace(expression))
        {
            if (!hasFilter)
                return null;

            resultSet = FacetFilterEvaluator.BuildMatchingBitSet(filter!, snapshot, queryContext);
            snapshot.RemoveDeadOrdinals(resultSet);
            return resultSet;
        }

        resultSet = EvaluateExpression(expression, method, enableOperators, queryContext, snapshot);
        if (resultSet is null)
            return null;

        snapshot.RemoveDeadOrdinals(resultSet);
        if (hasFilter)
            FacetFilterEvaluator.Apply(resultSet, filter!, snapshot, queryContext);

        return resultSet;
    }

    // The expression is not blank: Evaluate answers a blank one from the filter alone.
    private static FastBitSet? EvaluateExpression(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        QueryContext queryContext,
        IndexSnapshot snapshot)
    {
        if (method == WordMatchMethod.Regex)
            return RegexMatcher.MatchRegex(expression, queryContext, snapshot);

        if (QueryExpressionEvaluator.TryGetSingleSemanticWord(
                expression.AsSpan(),
                enableOperators,
                snapshot.Tokenization,
                out var singleWord))
            return QueryMatcher.Match(singleWord!, method, queryContext, snapshot);

        return QueryExpressionEvaluator.Evaluate(expression, enableOperators, method, queryContext, snapshot);
    }
}
