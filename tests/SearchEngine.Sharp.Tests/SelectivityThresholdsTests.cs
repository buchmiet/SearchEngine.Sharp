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
    public async Task UseSortKPath_MatchesBdnCrossoverMargin(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseSortKPath(hits, documents)).IsEqualTo(expected);

    [Test]
    [Arguments(10, 100_000, true)]
    [Arguments(50_000, 100_000, true)]
    [Arguments(50_001, 100_000, false)]
    [Arguments(100_000, 100_000, false)]
    public async Task UseFacetOnHitsPath_MatchesBdnCrossover(int hits, int documents, bool expected)
        => await Assert.That(SelectivityThresholds.UseFacetOnHitsPath(hits, documents)).IsEqualTo(expected);
}
