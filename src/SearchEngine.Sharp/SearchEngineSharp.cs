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
    public List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode)
    {
        // Get snapshot reference once - atomic read
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

        // Collect results — use pre-sorted permutation or snapshot order
        var results = new List<int>();
        var recordIdsSpan = snapshot.RecordIds.AsSpan();

        if (sortMode == SearchSortMode.NaturalSortAscending)
        {
            // Linear scan of pre-computed permutation — O(n), no per-query Sort
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
