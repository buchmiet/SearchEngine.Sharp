namespace SearchEngine.Query;

// Hit fraction limits that decide whether the natural sort and the facet filter work on the hits only or on every
// document. Each limit sits at or below the crossover its benchmark measures.
internal static class SelectivityThresholds
{
    // Largest hit fraction at which the natural sort sorts the hits while the base's sort permutation is not built.
    // Sorting the hits is faster than building the permutation and walking it up to about 90%
    // (NaturalSortColdSelectivityBenchmark, 100,000 documents); 80% leaves a margin.
    internal const double NaturalSortSortKMaxFraction = 0.80;

    // Largest hit fraction at which the natural sort sorts the hits once the base's sort permutation is built. Walking
    // the built permutation is faster above about 0.5% (NaturalSortSelectivityBenchmark, 100,000 documents).
    internal const double NaturalSortBuiltPermutationSortKMaxFraction = 0.005;

    // Largest hit fraction at which the facet filter tests only the hits. Faster than the full scan up to about 80%
    // (FacetSelectivityBenchmark, 100,000 documents, filter passing half of them); 75% leaves a margin.
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
