using SearchEngine.Index;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Result sets of a chosen size for the benchmarks that vary the number of hits.
/// </summary>
internal static class HitSets
{
    /// <summary>The hit count of <paramref name="density"/> (0 to 1) of <paramref name="documentCount"/> documents.</summary>
    internal static int CountFor(double density, int documentCount) => (int)Math.Round(density * documentCount);

    /// <summary>
    /// Creates a set of exactly <paramref name="hitCount"/> hits at ordinals chosen uniformly at random, as a text
    /// query scatters its hits over the index.
    /// </summary>
    internal static FastBitSet Random(int documentCount, int hitCount, int seed)
    {
        var bitSet = new FastBitSet(documentCount);
        bitSet.AddAll(RandomOrdinals(documentCount, hitCount, seed));
        return bitSet;
    }

    /// <summary>Returns <paramref name="hitCount"/> distinct ordinals below <paramref name="documentCount"/>, in random order.</summary>
    internal static int[] RandomOrdinals(int documentCount, int hitCount, int seed)
    {
        int count = Math.Clamp(hitCount, 0, documentCount);
        var ordinals = new int[documentCount];
        for (int i = 0; i < documentCount; i++)
            ordinals[i] = i;

        // Partial Fisher-Yates shuffle: the first count entries are a uniform sample.
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int j = random.Next(i, documentCount);
            (ordinals[i], ordinals[j]) = (ordinals[j], ordinals[i]);
        }

        return ordinals[..count];
    }
}
