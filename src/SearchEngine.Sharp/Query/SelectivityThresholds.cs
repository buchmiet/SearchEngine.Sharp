namespace SearchEngine.Query;

/// <summary>
/// Density thresholds that decide, from the share of documents a query matches, whether the sort and facet
/// stages work on the hit list only or on the whole index. Measured on a corpus of 100,000 names.
/// </summary>
internal static class SelectivityThresholds
{
    /// <summary>
    /// Largest hit fraction for which sorting only the hits (sort-K) is used while the snapshot's sort permutation is
    /// not built yet. Sort-K is faster than building the permutation and walking it up to about 90% density; 80%
    /// leaves a margin.
    /// </summary>
    internal const double NaturalSortSortKMaxFraction = 0.80;

    /// <summary>
    /// Largest hit fraction for which sort-K is used once the snapshot's sort permutation is built. Walking the built
    /// permutation is faster above about 0.5% density (NaturalSortSelectivityBenchmark).
    /// </summary>
    internal const double NaturalSortBuiltPermutationSortKMaxFraction = 0.005;

    /// <summary>
    /// Largest hit fraction for which facet filtering runs on the hits only. Faster than the full scan up to about 80%
    /// density (FacetSelectivityBenchmark, 100,000 documents, filter passing half of them); 75% leaves a margin.
    /// </summary>
    internal const double FacetOnHitsMaxFraction = 0.75;

    internal static bool UseSortKPath(int hitCount, int documentCount, bool permutationBuilt)
        => hitCount > 0
           && hitCount < documentCount
           && hitCount <= (int)(documentCount * (permutationBuilt
               ? NaturalSortBuiltPermutationSortKMaxFraction
               : NaturalSortSortKMaxFraction));

    internal static bool UseFacetOnHitsPath(int hitCount, int documentCount)
        => hitCount > 0
           && hitCount < documentCount
           && hitCount <= (int)(documentCount * FacetOnHitsMaxFraction);
}
