namespace SearchEngine.Query;

/// <summary>
/// Density thresholds that decide, from the share of documents a query matches, whether the sort and facet
/// stages work on the hit list only or on the whole index. Measured on a corpus of 100,000 names.
/// </summary>
internal static class SelectivityThresholds
{
    /// <summary>
    /// Largest hit fraction for which sorting only the hits (sort-K) is used. Sort-K is faster up to about 90%
    /// density; 80% leaves a margin.
    /// </summary>
    internal const double NaturalSortSortKMaxFraction = 0.80;

    /// <summary>Largest hit fraction for which facet filtering runs on the hits only. Faster up to about 50% density.</summary>
    internal const double FacetOnHitsMaxFraction = 0.50;

    internal static bool UseSortKPath(int hitCount, int documentCount)
        => hitCount > 0
           && hitCount < documentCount
           && hitCount <= (int)(documentCount * NaturalSortSortKMaxFraction);

    internal static bool UseFacetOnHitsPath(int hitCount, int documentCount)
        => hitCount > 0
           && hitCount < documentCount
           && hitCount <= (int)(documentCount * FacetOnHitsMaxFraction);
}
