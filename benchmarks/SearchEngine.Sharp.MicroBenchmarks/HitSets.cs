using SearchEngine.Index;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// Result sets of a chosen size for the benchmarks that vary the number of hits.
/// </summary>
internal static class HitSets
{
    /// <summary>
    /// Creates a set of <paramref name="hitCount"/> hits spread evenly over <paramref name="documentCount"/> ordinals.
    /// </summary>
    internal static FastBitSet Sparse(int documentCount, int hitCount)
    {
        var bitSet = new FastBitSet(documentCount);
        if (hitCount <= 0)
            return bitSet;

        int k = Math.Min(hitCount, documentCount);
        if (k >= documentCount)
        {
            bitSet.FillAllTrue();
            return bitSet;
        }

        // Spread hits across the index to avoid artificially dense low-ordinal clustering.
        int stride = Math.Max(1, documentCount / k);
        for (int i = 0, added = 0; added < k; i += stride, added++)
            bitSet.Add(i);

        return bitSet;
    }
}
