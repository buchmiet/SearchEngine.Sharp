using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

public class SelectivityThresholdsTests
{
    [Test]
    [Arguments(0, 100_000, false)]
    [Arguments(1, 100_000, true)]
    [Arguments(80_000, 100_000, true)]
    [Arguments(80_001, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task UseSortKPath_without_the_permutation_matches_BdnCrossoverMargin(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseSortKPath(hits, documents, permutationBuilt: false)).IsEqualTo(expected);

    [Test]
    [Arguments(0, 100_000, false)]
    [Arguments(1, 100_000, true)]
    [Arguments(500, 100_000, true)]
    [Arguments(501, 100_000, false)]
    [Arguments(50_000, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task UseSortKPath_with_the_built_permutation_matches_BdnCrossover(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseSortKPath(hits, documents, permutationBuilt: true)).IsEqualTo(expected);

    [Test]
    [Arguments(10, 100_000, true)]
    [Arguments(75_000, 100_000, true)]
    [Arguments(75_001, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task UseFacetOnHitsPath_MatchesBdnCrossover(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseFacetOnHitsPath(hits, documents)).IsEqualTo(expected);
}
