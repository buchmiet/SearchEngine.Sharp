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
    private const string FileMaskQuerySeparators = " \r\n"; // explicit CR/LF, not Environment.NewLine: query semantics must be identical on every OS

    /// <summary>
    /// Default preset: token-level search. Indexed text splits on whitespace and on the separator characters
    /// listed in <see cref="IndexSeparators"/> (<c>. , ; : / \ | - # [ ] { } ( ) ~ £ $ €</c>); query terms split on
    /// the characters listed in <see cref="QuerySeparators"/>.
    /// </summary>
    public static SearchTokenization Default { get; } = new(DefaultIndexSeparators, DefaultQuerySeparators);

    /// <summary>
    /// File-mask preset: the whole <see cref="IndexedEntry.SearchText"/> is one index token;
    /// query terms split on whitespace only.
    /// </summary>
    public static SearchTokenization FileMask { get; } = new(string.Empty, FileMaskQuerySeparators);

    private SearchTokenization(string indexSeparators, string querySeparators, UnicodeMatching unicodeMatching = UnicodeMatching.None)
    {
        UnicodeMatching = unicodeMatching;
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

    /// <summary>Unicode matching options of this preset (<see cref="UnicodeMatching.None"/> for the built-in presets).</summary>
    public UnicodeMatching UnicodeMatching { get; }

    /// <summary>
    /// Returns a preset with the same separators and the given Unicode matching options, for example
    /// <c>SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold)</c>.
    /// The options are stored in each snapshot, so indexed text and queries are always folded alike.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// <paramref name="options"/> contains <see cref="UnicodeMatching.Compose"/> or
    /// <see cref="UnicodeMatching.RemoveDiacritics"/> and the process runs in globalization-invariant mode, which has
    /// no Unicode normalization.
    /// </exception>
    public SearchTokenization WithUnicodeMatching(UnicodeMatching options)
    {
        if ((options & (UnicodeMatching.Compose | UnicodeMatching.RemoveDiacritics)) != 0 && !TextFolder.IsNormalizationAvailable)
        {
            throw new PlatformNotSupportedException(
                "UnicodeMatching.Compose and UnicodeMatching.RemoveDiacritics need Unicode normalization, which is not "
                + "available in globalization-invariant mode (InvariantGlobalization or DOTNET_SYSTEM_GLOBALIZATION_INVARIANT).");
        }

        return new(IndexSeparators, QuerySeparators, options);
    }

    internal string FoldIndexText(string text) => TextFolder.Fold(text, UnicodeMatching);

    // One query term, after operators have been recognized on the query as typed. Regex patterns are never folded.
    internal ReadOnlySpan<char> FoldQueryTerm(ReadOnlySpan<char> term)
        => (UnicodeMatching & TextFolder.FoldingOptions) == 0 || System.Text.Ascii.IsValid(term)
            ? term
            : TextFolder.Fold(term.ToString(), UnicodeMatching);

    internal bool GraphemeBoundaries => (UnicodeMatching & UnicodeMatching.GraphemeBoundaries) != 0;

    /// <summary>Creates a custom preset from explicit separator strings.</summary>
    public static SearchTokenization Create(string indexSeparators, string querySeparators)
        => new(indexSeparators ?? string.Empty, querySeparators ?? string.Empty);
}
