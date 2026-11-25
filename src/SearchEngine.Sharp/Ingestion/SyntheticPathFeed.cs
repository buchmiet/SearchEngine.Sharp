using System.Runtime.CompilerServices;
using SearchEngine.Tokenizer;

namespace SearchEngine.Ingestion;

/// <summary>
/// Generates synthetic file paths for tests, demos and benchmarks. The same seed gives the same paths.
/// </summary>
/// <remarks>
/// Each path has 2 to 5 folders named <c>folder0</c> to <c>folder199</c> and a file
/// <c>file{id}_{nnnn}.txt</c>. It is returned as passed through <see cref="Tokenizer.NameTokenizer.TokenizeName"/>,
/// so it holds 5 to 8 tokens joined with <c>-</c> (fewer when a folder name repeats). The ids run from 0 to
/// <c>count - 1</c>.
/// </remarks>
public static class SyntheticPathFeed
{
    /// <summary>
    /// Yields <paramref name="count"/> synthetic paths, optionally waiting before each one to simulate slow I/O.
    /// </summary>
    /// <param name="count">The number of paths. Zero or less yields nothing.</param>
    /// <param name="seed">The seed of the random generator.</param>
    /// <param name="delayPerEntry">The wait before each path; no wait when zero or negative.</param>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    /// <returns>The paths, keyed by id.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async IAsyncEnumerable<KeyValuePair<int, string>> EnumerateAsync(
        int count,
        int seed = 42,
        TimeSpan delayPerEntry = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rng = new Random(seed);

        for (int id = 0; id < count; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (delayPerEntry > TimeSpan.Zero)
                await Task.Delay(delayPerEntry, cancellationToken).ConfigureAwait(false);

            yield return new KeyValuePair<int, string>(id, TokenizePath(rng, id));
        }
    }

    /// <summary>
    /// Returns <paramref name="count"/> synthetic paths at once, for example for a one-shot
    /// <see cref="IIndexUpdater.RebuildFrom(IDictionary{int, string})"/>. The paths equal those of
    /// <see cref="EnumerateAsync"/> with the same seed.
    /// </summary>
    /// <param name="count">The number of paths.</param>
    /// <param name="seed">The seed of the random generator.</param>
    /// <returns>A new dictionary of paths keyed by id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static Dictionary<int, string> CreateDictionary(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var dict = new Dictionary<int, string>(count);

        for (int id = 0; id < count; id++)
            dict[id] = TokenizePath(rng, id);

        return dict;
    }

    private static string TokenizePath(Random rng, int id)
    {
        int depth = rng.Next(2, 6);
        var parts = new string[depth + 1];

        for (int i = 0; i < depth; i++)
            parts[i] = $"folder{rng.Next(0, 200)}";

        parts[depth] = $"file{id}_{rng.Next(1000, 9999)}.txt";
        return NameTokenizer.TokenizeName(string.Join('\\', parts), "-");
    }
}
