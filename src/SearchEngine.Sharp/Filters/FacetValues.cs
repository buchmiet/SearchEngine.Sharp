using System.Buffers;

namespace SearchEngine.Filters;

/// <summary>
/// Immutable facet name/value pairs attached to an indexed document.
/// </summary>
/// <remarks>
/// Stored as two small parallel arrays rather than a dictionary: a document carries a handful of
/// facets, the index builder only enumerates them, and one instance is created per indexed entry,
/// so per-entry allocation matters more than lookup speed.
/// </remarks>
public sealed class FacetValues
{
    /// <summary>
    /// Empty facet bag with no entries.
    /// </summary>
    public static FacetValues Empty { get; } = new([], []);

    private readonly string[] _names;
    private readonly long[] _values;

    private FacetValues(string[] names, long[] values)
    {
        _names = names;
        _values = values;
    }

    /// <summary>
    /// Creates facet values from a dictionary. Returns <see langword="null"/> when empty.
    /// </summary>
    /// <remarks>
    /// The entries are enumerated once into a buffer, so the result is consistent even when
    /// <paramref name="values"/> is a concurrent dictionary that changes during the call.
    /// Names are validated like <see cref="Create(ReadOnlySpan{KeyValuePair{string, long}})"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">A facet name is null or appears more than once.</exception>
    public static FacetValues? FromDictionary(IReadOnlyDictionary<string, long>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        var pool = ArrayPool<KeyValuePair<string, long>>.Shared;
        var buffer = pool.Rent(values.Count);
        int count = 0;
        try
        {
            foreach (var pair in values)
            {
                if (count == buffer.Length)
                {
                    var larger = pool.Rent(count * 2);
                    buffer.AsSpan(0, count).CopyTo(larger);
                    pool.Return(buffer, clearArray: true);
                    buffer = larger;
                }

                buffer[count++] = pair;
            }

            return count == 0 ? null : Create(buffer.AsSpan(0, count));
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Creates facet values from a single pair.
    /// </summary>
    public static FacetValues Create(string facet, long value)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return new([facet], [value]);
    }

    /// <summary>
    /// Creates facet values from name/value pairs without an intermediate dictionary.
    /// Returns <see cref="Empty"/> when <paramref name="values"/> is empty.
    /// </summary>
    /// <exception cref="ArgumentException">A facet name is null or appears more than once.</exception>
    public static FacetValues Create(params ReadOnlySpan<KeyValuePair<string, long>> values)
    {
        if (values.IsEmpty)
            return Empty;

        var names = new string[values.Length];
        var longs = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            string name = values[i].Key
                ?? throw new ArgumentException("Facet names must not be null.", nameof(values));
            if (Array.IndexOf(names, name, 0, i) >= 0)
                throw new ArgumentException($"Duplicate facet '{name}'.", nameof(values));

            names[i] = name;
            longs[i] = values[i].Value;
        }

        return new FacetValues(names, longs);
    }

    /// <summary>Number of facet pairs.</summary>
    internal int Count => _names.Length;

    internal string NameAt(int index) => _names[index];

    internal long ValueAt(int index) => _values[index];

    internal bool IsEmpty => _names.Length == 0;
}
