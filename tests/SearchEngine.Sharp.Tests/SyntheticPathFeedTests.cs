using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

// SyntheticPathFeed is public so applications and benchmarks can generate a reproducible scan
// (src/Ingestion/SyntheticPathFeed.cs). Its arguments are checked like those of the rest of the public API: a
// negative count throws at the call, naming count, and zero yields nothing. What it generates is used, and so
// checked, by ProgressiveIndexIngestionTests.
public class SyntheticPathFeedTests
{
    [Test]
    public async Task EnumerateAsync_rejects_a_negative_count_at_the_call()
    {
        // Thrown by the call itself, before the stream is enumerated, like CreateDictionary.
        var exception = await Assert.That(() => SyntheticPathFeed.EnumerateAsync(-1)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo("count");
    }

    [Test]
    public async Task CreateDictionary_names_count_for_a_negative_count()
    {
        var exception = await Assert.That(() => SyntheticPathFeed.CreateDictionary(-1)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo("count");
    }

    [Test]
    public async Task EnumerateAsync_with_zero_count_yields_nothing()
    {
        int yielded = 0;
        await foreach (var _ in SyntheticPathFeed.EnumerateAsync(0))
            yielded++;

        await Assert.That(yielded).IsEqualTo(0);
    }
}
