using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

// White-box: the hit fraction limits that choose between sorting the hits and walking the sort permutation, and
// between filtering the hits and scanning every document (SelectivityThresholds, where each limit names the benchmark
// that measured it). They change only the speed of a query; the results of both strategies are compared in
// NaturalSortTieBreakTests and FacetFilterTests. No hits and all hits never take the hits-only strategy.
public class SelectivityThresholdsTests
{
    [Test]
    [Arguments(0, 100_000, false)]
    [Arguments(1, 100_000, true)]
    [Arguments(80_000, 100_000, true)]
    [Arguments(80_001, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task Without_the_sort_permutation_the_hits_are_sorted_up_to_80_percent(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseSortKPath(hits, documents, permutationBuilt: false)).IsEqualTo(expected);

    [Test]
    [Arguments(0, 100_000, false)]
    [Arguments(1, 100_000, true)]
    [Arguments(500, 100_000, true)]
    [Arguments(501, 100_000, false)]
    [Arguments(50_000, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task With_the_built_sort_permutation_the_hits_are_sorted_up_to_half_a_percent(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseSortKPath(hits, documents, permutationBuilt: true)).IsEqualTo(expected);

    [Test]
    [Arguments(10, 100_000, true)]
    [Arguments(75_000, 100_000, true)]
    [Arguments(75_001, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task A_facet_filter_tests_only_the_hits_up_to_75_percent(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseFacetOnHitsPath(hits, documents)).IsEqualTo(expected);
}
