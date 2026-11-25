using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

public class IngestPublishPolicyTests
{
    [Test]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(double.NegativeInfinity)]
    [Arguments(0.0)]
    [Arguments(-1.0)]
    public async Task AdaptiveMultiplier_RejectsValuesThatAreNotFiniteAndPositive(double multiplier)
    {
        var ingestion = new ProgressiveIndexIngestion(new IndexUpdater(new IndexSnapshotProvider()));
        var options = new IngestPublishOptions { AdaptiveMultiplier = multiplier };

        await AssertOutOfRange("AdaptiveMultiplier", () => ingestion.IngestAsync(SyntheticPathFeed.EnumerateAsync(1), options));
    }

    private static async Task AssertOutOfRange(string paramName, Action call)
    {
        var exception = await Assert.That(call).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo(paramName);
    }
}
