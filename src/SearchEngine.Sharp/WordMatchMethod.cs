namespace SearchEngine;

/// <summary>
/// Specifies how search terms should be matched against indexed words.
/// </summary>
public enum WordMatchMethod
{
    /// <summary>
    /// Words must match exactly. Uses O(1) dictionary lookup.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Search term can be a substring of indexed words.
    /// </summary>
    Within = 1,

    /// <summary>
    /// The entire expression is one .NET regular expression matched against indexed tokens
    /// via standard unanchored <see cref="Regex.IsMatch(ReadOnlySpan{char})"/>.
    /// Boolean parsing and token separators are bypassed.
    /// </summary>
    Regex = 2,
}
