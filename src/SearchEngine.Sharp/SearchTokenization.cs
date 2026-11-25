using System.Buffers;

namespace SearchEngine;

/// <summary>
/// The separator characters that split indexed text and queries into tokens, plus the Unicode options.
/// Each <see cref="Snapshots.IndexSnapshot"/> stores the preset it was built with, and queries always use the
/// preset of the snapshot they run on.
/// </summary>
/// <remarks>Instances are immutable and safe to share between threads.</remarks>
public sealed class SearchTokenization
{
    private const string DefaultIndexSeparators = " .,;:/\\\r\n\t|-#[]{}()~£$€";
    private const string DefaultQuerySeparators = "| -./\n\r\t\\,#[]{}()~:;$£€";
    private const string FileMaskQuerySeparators = " \r\n"; // explicit CR/LF, not Environment.NewLine: query semantics must be identical on every OS

    /// <summary>
    /// Gets the preset for token-level search. Indexed text splits on space, tab, CR, LF and
    /// <c>. , ; : / \ | - # [ ] { } ( ) ~ £ $ €</c>. Queries split on the same characters, so a query pasted with
    /// a tab between its words finds them like a query with spaces.
    /// </summary>
    public static SearchTokenization Default { get; } = new(DefaultIndexSeparators, DefaultQuerySeparators);

    /// <summary>
    /// Gets the preset for file masks: the whole <see cref="IndexedEntry.SearchText"/> is one token, and queries
    /// split on space, CR and LF only, so <c>*.pdf</c> matches names that end in <c>.pdf</c>. A tab is part of the
    /// mask, so a name that contains a tab can be matched literally.
    /// </summary>
    public static SearchTokenization FileMask { get; } = new(string.Empty, FileMaskQuerySeparators);

    private SearchTokenization(string indexSeparators, string querySeparators, UnicodeMatching unicodeMatching = UnicodeMatching.None)
    {
        UnicodeMatching = unicodeMatching;
        IndexSeparators = indexSeparators;
        QuerySeparators = querySeparators;
        IndexSeparatorValues = SearchValues.Create(indexSeparators);
        QuerySeparatorValues = SearchValues.Create(querySeparators);
        ParenthesesGroup = querySeparators.Contains('(') && querySeparators.Contains(')');
    }

    /// <summary>Gets the characters that split indexed text into tokens.</summary>
    public string IndexSeparators { get; }

    /// <summary>Gets the characters that split a query into terms.</summary>
    public string QuerySeparators { get; }

    internal SearchValues<char> IndexSeparatorValues { get; }

    internal SearchValues<char> QuerySeparatorValues { get; }

    // Parentheses can group terms only when both are query separators; otherwise a separator parenthesis splits
    // terms like a space and the other one is a character of its term.
    internal bool ParenthesesGroup { get; }

    /// <summary>Gets the Unicode options of this preset. <see cref="UnicodeMatching.None"/> for <see cref="Default"/>,
    /// <see cref="FileMask"/> and presets made by <see cref="Create"/>.</summary>
    public UnicodeMatching UnicodeMatching { get; }

    /// <summary>
    /// Returns a preset with the same separators and the given Unicode options, for example
    /// <c>SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold)</c>.
    /// The options are stored in each snapshot, so indexed text and queries are always folded alike.
    /// </summary>
    /// <param name="options">The options of the new preset. They replace the options of this preset.</param>
    /// <returns>A new preset; this instance is unchanged.</returns>
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

    // Indexed text, split at the index separators as written and folded token by token (TextFolder.FoldRuns).
    internal string FoldIndexText(string text) => TextFolder.FoldRuns(text, UnicodeMatching, IndexSeparatorValues);

    // The entry as IndexUpdater stores it: SearchText folded once, when the entry comes in, so that rebuilds after
    // later changes do not fold it again (IndexSnapshotBuilder.BuildFolded). SortText and Facets stay as given.
    internal IndexedEntry FoldEntry(IndexedEntry entry)
    {
        string folded = FoldIndexText(entry.SearchText);
        return ReferenceEquals(folded, entry.SearchText) ? entry : entry with { SearchText = folded };
    }

    // One query term, split at the query separators as written, after operators have been recognized on the query
    // as typed. Regex patterns are never folded.
    internal ReadOnlySpan<char> FoldQueryTerm(ReadOnlySpan<char> term)
        => (UnicodeMatching & TextFolder.FoldingOptions) == 0 || System.Text.Ascii.IsValid(term)
            ? term
            : TextFolder.Fold(term.ToString(), UnicodeMatching, QuerySeparatorValues);

    internal bool GraphemeBoundaries => (UnicodeMatching & UnicodeMatching.GraphemeBoundaries) != 0;

    /// <summary>Creates a preset with the given separator characters and no Unicode options.</summary>
    /// <param name="indexSeparators">The characters that split indexed text into tokens. An empty string makes the
    /// whole text one token.</param>
    /// <param name="querySeparators">The characters that split a query into terms. An empty string makes the whole
    /// query one term.</param>
    /// <returns>A new preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="indexSeparators"/> or <paramref name="querySeparators"/>
    /// is <see langword="null"/>.</exception>
    public static SearchTokenization Create(string indexSeparators, string querySeparators)
    {
        ArgumentNullException.ThrowIfNull(indexSeparators);
        ArgumentNullException.ThrowIfNull(querySeparators);
        return new(indexSeparators, querySeparators);
    }
}
