namespace SearchEngine.Filters;

/// <summary>
/// Immutable facet name/value pairs attached to an indexed document.
/// </summary>
public sealed class FacetValues
{
    private static readonly IReadOnlyDictionary<string, long> EmptyDictionary =
        new Dictionary<string, long>();

    /// <summary>
    /// Empty facet bag with no entries.
    /// </summary>
    public static FacetValues Empty { get; } = new(EmptyDictionary);

    private readonly Dictionary<string, long> _values;

    private FacetValues(IReadOnlyDictionary<string, long> values)
    {
        _values = new Dictionary<string, long>(values, StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates facet values from a dictionary. Returns <see langword="null"/> when empty.
    /// </summary>
    public static FacetValues? FromDictionary(IReadOnlyDictionary<string, long>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        return new FacetValues(values);
    }

    /// <summary>
    /// Creates facet values from a single pair.
    /// </summary>
    public static FacetValues Create(string facet, long value)
        => new(new Dictionary<string, long>(1, StringComparer.Ordinal) { [facet] = value });

    internal IReadOnlyDictionary<string, long> Values => _values;

    internal bool IsEmpty => _values.Count == 0;
}
