using System.Buffers;

namespace SearchEngine.Filters;

/// <summary>
/// The named <see langword="long"/> values of one indexed document, tested by a <see cref="FacetFilter"/>.
/// Names compare ordinally, so they are case-sensitive.
/// </summary>
/// <remarks>
/// Instances are immutable and safe to share between threads and documents. They hold two small arrays and no
/// dictionary, because the index builder only enumerates them and one instance is created per entry.
/// </remarks>
public sealed class FacetValues
{
    /// <summary>
    /// Gets an instance with no values.
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
    /// Creates facet values from the pairs of a dictionary.
    /// </summary>
    /// <param name="values">The facet names and values, or <see langword="null"/>.</param>
    /// <returns>The facet values, or <see langword="null"/> when <paramref name="values"/> is
    /// <see langword="null"/> or empty.</returns>
    /// <remarks>
    /// The dictionary is enumerated once, so the result is consistent even when <paramref name="values"/> is a
    /// concurrent dictionary that another thread changes during the call.
    /// </remarks>
    /// <exception cref="ArgumentException">A facet name is <see langword="null"/> or appears more than
    /// once.</exception>
    public static FacetValues? FromDictionary(IReadOnlyDictionary<string, long>? values)
    {
        // Count is read once and only sizes the buffer: a concurrent dictionary can report another count on every
        // read, so the enumeration alone decides what is copied.
        int expected = values?.Count ?? 0;
        if (expected == 0)
            return null;

        var pool = ArrayPool<KeyValuePair<string, long>>.Shared;
        var buffer = pool.Rent(expected);
        int count = 0;
        try
        {
            foreach (var pair in values!)
            {
                if (count == buffer.Length)
                {
                    var larger = pool.Rent(Math.Max(4, count * 2));
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
    /// Creates facet values with one pair.
    /// </summary>
    /// <param name="facet">The facet name.</param>
    /// <param name="value">The value of the facet.</param>
    /// <returns>A new instance with one pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facet"/> is <see langword="null"/>.</exception>
    public static FacetValues Create(string facet, long value)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return new([facet], [value]);
    }

    /// <summary>
    /// Creates facet values from name and value pairs without an intermediate dictionary.
    /// </summary>
    /// <param name="values">The facet names and values.</param>
    /// <returns>A new instance, or <see cref="Empty"/> when <paramref name="values"/> is empty.</returns>
    /// <exception cref="ArgumentException">A facet name is <see langword="null"/> or appears more than
    /// once.</exception>
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

        return new(names, longs);
    }

    /// <summary>Number of facet pairs.</summary>
    internal int Count => _names.Length;

    internal string NameAt(int index) => _names[index];

    internal long ValueAt(int index) => _values[index];

    internal bool IsEmpty => _names.Length == 0;
}
