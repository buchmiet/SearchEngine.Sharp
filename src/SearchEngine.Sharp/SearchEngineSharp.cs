using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Stateless search engine that executes queries against the current snapshot.
/// Thread-safe for concurrent queries.
/// </summary>
public sealed class SearchEngineSharp(IIndexSnapshotProvider snapshotProvider) : ISearchEngine
{
    /// <inheritdoc />
    public int DocumentCount => snapshotProvider.Current.DocumentCount;

    /// <inheritdoc />
    public int UniqueWordCount => snapshotProvider.Current.UniqueWordCount;

    /// <inheritdoc />
    public List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false)
    {
        return Find(expression, method, enableOperators, SearchSortMode.SnapshotOrder);
    }

    /// <inheritdoc />
    public int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false)
    {
        var snapshot = snapshotProvider.Current;
        if (string.IsNullOrWhiteSpace(expression) || snapshot.DocumentCount == 0)
            return 0;

        if (TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
            return exactMatches.Length;

        using var queryContext = new QueryContext(snapshot.DocumentCount);
        var resultSet = EvaluateExpression(expression, method, enableOperators, queryContext, snapshot);
        return resultSet?.GetTrueCount() ?? 0;
    }

    /// <inheritdoc />
    public int CountMatches(string expression, WordMatchMethod method, bool enableOperators, FacetFilter? filter)
    {
        var snapshot = snapshotProvider.Current;
        if (!HasQueryInput(expression, filter) || snapshot.DocumentCount == 0)
            return 0;

        if (filter is not null && !filter.IsEmpty)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return FacetFilterEvaluator.CountFilterOnly(filter, snapshot);

            if (TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
                return FacetFilterEvaluator.CountMatchingOrdinals(filter, snapshot, exactMatches);
        }

        using var queryContext = new QueryContext(snapshot.DocumentCount);
        var resultSet = ExecuteQuery(snapshot, expression, method, enableOperators, filter, queryContext);
        return resultSet?.GetTrueCount() ?? 0;
    }

    /// <inheritdoc />
    public List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode)
    {
        var snapshot = snapshotProvider.Current;
        if (string.IsNullOrWhiteSpace(expression) || snapshot.DocumentCount == 0)
            return [];

        if (sortMode == SearchSortMode.SnapshotOrder
            && TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
            return PostingListOperations.MaterializeRecordIds(snapshot.RecordIds, exactMatches);

        using var queryContext = new QueryContext(snapshot.DocumentCount);
        var resultSet = EvaluateExpression(expression, method, enableOperators, queryContext, snapshot);
        if (resultSet == null)
            return [];

        return MaterializeResults(snapshot, resultSet, sortMode);
    }

    /// <inheritdoc />
    public List<int> Find(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        SearchSortMode sortMode,
        FacetFilter? filter)
    {
        var snapshot = snapshotProvider.Current;
        if (!HasQueryInput(expression, filter) || snapshot.DocumentCount == 0)
            return [];

        if (filter is not null && !filter.IsEmpty)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return FacetFilterEvaluator.MaterializeFilterOnly(filter, snapshot, sortMode);

            if (sortMode == SearchSortMode.SnapshotOrder
                && TryGetExactWordPostings(expression, method, enableOperators, snapshot, out var exactMatches))
                return FacetFilterEvaluator.MaterializeMatchingRecordIds(filter, snapshot, exactMatches);
        }

        using var queryContext = new QueryContext(snapshot.DocumentCount);
        var resultSet = ExecuteQuery(snapshot, expression, method, enableOperators, filter, queryContext);
        if (resultSet == null)
            return [];

        return MaterializeResults(snapshot, resultSet, sortMode);
    }

    private static bool HasQueryInput(string expression, FacetFilter? filter)
        => !string.IsNullOrWhiteSpace(expression) || filter is not null && !filter.IsEmpty;

    // Fast path of an Exact query for one plain word (no glob metacharacters): its postings are read straight from
    // the snapshot, without a bitset. False when the query is not such a word; empty postings when the word is not
    // indexed.
    private static bool TryGetExactWordPostings(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        IndexSnapshot snapshot,
        out ReadOnlySpan<int> postings)
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

    private static List<int> MaterializeResults(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        SearchSortMode sortMode)
        => ResultMaterializer.Materialize(snapshot, resultSet, sortMode);

    private static FastBitSet? ExecuteQuery(
        IndexSnapshot snapshot,
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        FacetFilter? filter,
        QueryContext queryContext)
    {
        FastBitSet? resultSet;
        if (string.IsNullOrWhiteSpace(expression))
        {
            if (filter is not null && !filter.IsEmpty)
                resultSet = FacetFilterEvaluator.BuildMatchingBitSet(filter, snapshot, queryContext);
            else
                return null;
        }
        else
        {
            resultSet = EvaluateExpression(expression, method, enableOperators, queryContext, snapshot);
            if (resultSet is null)
                return null;

            if (filter is not null && !filter.IsEmpty)
                FacetFilterEvaluator.Apply(resultSet, filter, snapshot, queryContext);
        }

        return resultSet;
    }

    private static FastBitSet? EvaluateExpression(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        QueryContext queryContext,
        IndexSnapshot snapshot)
    {
        if (method == WordMatchMethod.Regex)
            return string.IsNullOrWhiteSpace(expression)
                ? null
                : RegexMatcher.MatchRegex(expression, queryContext, snapshot);

        if (QueryExpressionEvaluator.TryGetSingleSemanticWord(
                expression.AsSpan(),
                enableOperators,
                snapshot.Tokenization,
                out var singleWord))
            return QueryMatcher.Match(singleWord!, method, queryContext, snapshot);

        return QueryExpressionEvaluator.Evaluate(expression, enableOperators, method, queryContext, snapshot);
    }
}
