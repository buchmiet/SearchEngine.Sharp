namespace SearchEngine;

/// <summary>
/// Specifies how search terms are matched against indexed tokens. Indexed tokens and terms are lowercased, so
/// matching is case-insensitive for every method.
/// </summary>
public enum WordMatchMethod
{
    /// <summary>
    /// A term must equal an indexed token. A term with <c>*</c> or <c>?</c> is a glob pattern that must match a
    /// whole token.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// A term can be a substring of an indexed token. A term with <c>*</c> or <c>?</c> is a glob pattern that
    /// must match a whole token, as under <see cref="Exact"/>.
    /// </summary>
    Within = 1,

    /// <summary>
    /// The whole expression is one .NET regular expression, matched against each indexed token with unanchored
    /// <see cref="System.Text.RegularExpressions.Regex.IsMatch(ReadOnlySpan{char})"/>; a document matches when one
    /// of its tokens does. Operators and query separators do not apply. The characters of the pattern that stand for
    /// themselves get the casing of indexed tokens (lowercase, and case folding under
    /// <see cref="UnicodeMatching.CaseFold"/>) from the Unicode tables of the library, and the pattern then runs
    /// case-sensitively, non-backtracking when the pattern allows it, with a one-second timeout for each token. An
    /// invalid pattern, or a timeout on any token, gives an empty result.
    /// </summary>
    Regex = 2,
}
