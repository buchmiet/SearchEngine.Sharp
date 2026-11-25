using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

public class SyntheticPathFeedTests
{
    [Fact]
    public void EnumerateAsync_rejects_a_negative_count_at_the_call()
    {
        // Thrown by the call itself, before the stream is enumerated, like CreateDictionary.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticPathFeed.EnumerateAsync(-1));
        Assert.Equal("count", exception.ParamName);
    }

    [Fact]
    public async Task EnumerateAsync_with_zero_count_yields_nothing()
    {
        int yielded = 0;
        await foreach (var _ in SyntheticPathFeed.EnumerateAsync(0))
            yielded++;

        Assert.Equal(0, yielded);
    }
}
