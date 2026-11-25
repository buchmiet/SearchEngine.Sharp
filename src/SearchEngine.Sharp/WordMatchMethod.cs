namespace SearchEngine;

/// <summary>
/// Specifies how search terms are matched against indexed words. Indexed words and terms are lowercased, so
/// matching is case-insensitive for every method.
/// </summary>
public enum WordMatchMethod
{
    /// <summary>
    /// A term must equal an indexed word; a term without wildcard characters is one O(1) dictionary lookup.
    /// A term with <c>*</c> or <c>?</c> is a glob pattern that must match a whole word.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// A term can be a substring of an indexed word. A term with <c>*</c> or <c>?</c> is a glob pattern that
    /// must match a whole word, as under <see cref="Exact"/>.
    /// </summary>
    Within = 1,

    /// <summary>
    /// The entire expression is one .NET regular expression matched against indexed tokens
    /// via standard unanchored <see cref="System.Text.RegularExpressions.Regex.IsMatch(ReadOnlySpan{char})"/>.
    /// Boolean parsing and token separators are bypassed. The pattern is compiled case-insensitive and, when the
    /// pattern supports it, non-backtracking, with a one-second match timeout. An invalid pattern or a timeout
    /// gives an empty result.
    /// </summary>
    Regex = 2,
}
