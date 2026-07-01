using System.Runtime.CompilerServices;
using SearchEngine.Tokenizer;

namespace SearchEngine.Ingestion;

/// <summary>
/// Generates synthetic file-path search text for ingestion experiments and demos.
/// </summary>
public static class SyntheticPathFeed
{
    /// <summary>
    /// Yields tokenized path search text with optional per-entry delay to simulate slow I/O.
    /// </summary>
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
    /// Builds a dictionary of tokenized paths (for one-shot rebuild baselines).
    /// </summary>
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
