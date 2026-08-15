using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

public class SelectivityThresholdsTests
{
    [Theory]
    [InlineData(0, 100_000, false)]
    [InlineData(1, 100_000, true)]
    [InlineData(80_000, 100_000, true)]
    [InlineData(80_001, 100_000, false)]
    [InlineData(100_000, 100_000, false)]
    public void UseSortKPath_MatchesBdnCrossoverMargin(int hits, int documents, bool expected)
        => Assert.Equal(expected, SelectivityThresholds.UseSortKPath(hits, documents));

    [Theory]
    [InlineData(10, 100_000, true)]
    [InlineData(50_000, 100_000, true)]
    [InlineData(50_001, 100_000, false)]
    [InlineData(100_000, 100_000, false)]
    public void UseFacetOnHitsPath_MatchesBdnCrossover(int hits, int documents, bool expected)
        => Assert.Equal(expected, SelectivityThresholds.UseFacetOnHitsPath(hits, documents));
}
