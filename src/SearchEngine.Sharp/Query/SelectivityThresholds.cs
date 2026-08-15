namespace SearchEngine.Query;

/// <summary>
/// Density thresholds for hybrid selectivity paths (BDN x64 @ 100k, pass 3.1).
/// </summary>
internal static class SelectivityThresholds
{
    /// <summary>Sort-K wins until ~90% density; use 80% conservative margin.</summary>
    internal const double NaturalSortSortKMaxFraction = 0.80;

    /// <summary>Facet-on-K wins until ~50% density.</summary>
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
