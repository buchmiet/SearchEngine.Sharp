using System.Buffers;

namespace SearchEngine;

/// <summary>
/// Index-side and query-side separator sets that define how text is tokenized for a snapshot.
/// Stored in each <see cref="Snapshots.IndexSnapshot"/> so queries always match the index preset.
/// </summary>
public sealed class SearchTokenization
{
    private const string DefaultIndexSeparators = " .,;:/\\\r\n\t|-#[]{}()~£$€";
    private const string DefaultQuerySeparators = "| -./\n\r\\,#[]{}()~:;$£€";
    private const string FileMaskQuerySeparators = " \r\n"; // explicit CR/LF, not Environment.NewLine — query semantics must be identical on every OS

    /// <summary>
    /// Current default preset: token-level semantics (same as pre-0.5.3 behavior).
    /// </summary>
    public static SearchTokenization Default { get; } = new(DefaultIndexSeparators, DefaultQuerySeparators);

    /// <summary>
    /// Classic file-mask preset: whole <see cref="IndexedEntry.SearchText"/> is one index token;
    /// query terms split on whitespace only.
    /// </summary>
    public static SearchTokenization FileMask { get; } = new(string.Empty, FileMaskQuerySeparators);

    private SearchTokenization(string indexSeparators, string querySeparators)
    {
        IndexSeparators = indexSeparators;
        QuerySeparators = querySeparators;
        IndexSeparatorValues = SearchValues.Create(indexSeparators);
        QuerySeparatorValues = SearchValues.Create(querySeparators);
    }

    /// <summary>Raw index-side separator characters (for inspection and docs).</summary>
    public string IndexSeparators { get; }

    /// <summary>Raw query-side separator characters (for inspection and docs).</summary>
    public string QuerySeparators { get; }

    internal SearchValues<char> IndexSeparatorValues { get; }

    internal SearchValues<char> QuerySeparatorValues { get; }

    /// <summary>Creates a custom preset from explicit separator strings.</summary>
    public static SearchTokenization Create(string indexSeparators, string querySeparators)
        => new(indexSeparators ?? string.Empty, querySeparators ?? string.Empty);
}
