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

        if (method == WordMatchMethod.Exact)
        {
            if (!enableOperators
                && QueryExpressionEvaluator.TryGetSingleWord(expression.AsSpan(), out var singleWord)
                && !GlobMatcher.ContainsMetacharacters(singleWord!))
            {
                return TryGetExactPostingSpan(singleWord!, snapshot, out var exactMatches)
                    ? exactMatches.Length
                    : 0;
            }
        }

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

        if (method == WordMatchMethod.Exact
            && !enableOperators
            && sortMode == SearchSortMode.SnapshotOrder
            && QueryExpressionEvaluator.TryGetSingleWord(expression.AsSpan(), out var singleWord)
            && !GlobMatcher.ContainsMetacharacters(singleWord!))
        {
            return TryGetExactPostingSpan(singleWord!, snapshot, out var exactMatches)
                ? PostingListOperations.MaterializeRecordIds(snapshot.RecordIds, exactMatches)
                : [];
        }

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

        using var queryContext = new QueryContext(snapshot.DocumentCount);
        var resultSet = ExecuteQuery(snapshot, expression, method, enableOperators, filter, queryContext);
        if (resultSet == null)
            return [];

        return MaterializeResults(snapshot, resultSet, sortMode);
    }

    private static bool HasQueryInput(string expression, FacetFilter? filter)
        => !string.IsNullOrWhiteSpace(expression) || filter is not null && !filter.IsEmpty;

    private static List<int> MaterializeResults(
        IndexSnapshot snapshot,
        FastBitSet resultSet,
        SearchSortMode sortMode)
    {
        var results = new List<int>();
        var recordIdsSpan = snapshot.RecordIds.AsSpan();

        if (sortMode == SearchSortMode.NaturalSortAscending)
        {
            var permutation = snapshot.GetSortedPermutation();
            foreach (var idx in permutation)
            {
                if (resultSet.Get(idx))
                    results.Add(recordIdsSpan[idx]);
            }
        }
        else
        {
            for (int k = 0; k < resultSet.Length; k++)
            {
                if (resultSet.Get(k))
                    results.Add(recordIdsSpan[k]);
            }
        }

        return results;
    }

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
            resultSet = queryContext.RentAllTrueBitSet();
        }
        else
        {
            resultSet = EvaluateExpression(expression, method, enableOperators, queryContext, snapshot);
        }

        if (resultSet is null)
            return null;

        if (filter is not null && !filter.IsEmpty)
            FacetFilterEvaluator.Apply(resultSet, filter, snapshot, queryContext);

        return resultSet;
    }

    private static bool TryGetExactPostingSpan(string word, IndexSnapshot snapshot, out ReadOnlySpan<int> postings)
    {
        if (snapshot.ExactPostings.TryGetValue(word, out var wordPostings))
        {
            postings = snapshot.PostingDocIds.AsSpan(wordPostings.Offset, wordPostings.Count);
            return true;
        }

        postings = default;
        return false;
    }

    private static FastBitSet? EvaluateExpression(
        string expression,
        WordMatchMethod method,
        bool enableOperators,
        QueryContext queryContext,
        IndexSnapshot snapshot)
    {
        if (!enableOperators && QueryExpressionEvaluator.TryGetSingleWord(expression.AsSpan(), out var singleWord))
            return QueryMatcher.Match(singleWord!, method, queryContext, snapshot);

        return QueryExpressionEvaluator.Evaluate(expression, enableOperators, method, queryContext, snapshot);
    }
}
