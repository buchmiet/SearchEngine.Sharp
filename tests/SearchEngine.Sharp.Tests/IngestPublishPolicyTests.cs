using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

// The publish settings check each value when it is set, in the init accessor, and throw an exception naming the
// property (src/Ingestion/IngestPublishPolicy.cs, IngestPublishOptions.cs), so a bad setting fails where it is written
// and not later inside a running ingestion; a `with` copy is checked the same way. The defaults are the ones the
// documentation promises.
public class IngestPublishPolicyTests
{
    [Test]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(double.NegativeInfinity)]
    [Arguments(0.0)]
    [Arguments(-1.0)]
    [Arguments(1_000.001)]
    [Arguments(double.MaxValue)]
    public async Task PublishMultiplier_rejects_values_outside_zero_to_the_limit(double multiplier)
    {
        await AssertOutOfRange("PublishMultiplier", () => _ = new IngestPublishPolicy.Adaptive { PublishMultiplier = multiplier });
    }

    [Test]
    public async Task PublishMultiplier_accepts_the_limit()
    {
        var policy = new IngestPublishPolicy.Adaptive { PublishMultiplier = IngestPublishPolicy.Adaptive.MaxPublishMultiplier };

        await Assert.That(policy.PublishMultiplier).IsEqualTo(1_000.0);
    }

    [Test]
    public async Task Every_setting_is_checked_when_it_is_set()
    {
        await AssertOutOfRange("BatchCap", () => _ = new IngestPublishPolicy.Adaptive { BatchCap = 0 });
        await AssertOutOfRange("MinTimerPublishBatch", () => _ = new IngestPublishPolicy.Adaptive { MinTimerPublishBatch = 0 });
        await AssertOutOfRange("MinInterval", () => _ = new IngestPublishPolicy.Adaptive { MinInterval = TimeSpan.FromTicks(-1) });
        await AssertOutOfRange("MaxStaleness", () => _ = new IngestPublishPolicy.Adaptive { MaxStaleness = TimeSpan.Zero });
        await AssertOutOfRange("MinInterval", () => _ = new IngestPublishPolicy.TimeDebounce { MinInterval = TimeSpan.FromTicks(-1) });
        await AssertOutOfRange("MaxStaleness", () => _ = new IngestPublishPolicy.TimeDebounce { MaxStaleness = TimeSpan.Zero });
        await AssertOutOfRange("BatchSize", () => _ = new IngestPublishPolicy.FixedBatch { BatchSize = 0 });
        await AssertOutOfRange("MaxStaleness", () => _ = new IngestPublishPolicy.FixedBatch { MaxStaleness = Timeout.InfiniteTimeSpan });
        await AssertOutOfRange("ChannelCapacity", () => _ = new IngestPublishOptions { ChannelCapacity = 0 });

        var exception = await Assert.That(() => _ = new IngestPublishOptions { Policy = null! }).ThrowsExactly<ArgumentNullException>();
        await Assert.That(exception!.ParamName).IsEqualTo("Policy");
    }

    [Test]
    public async Task A_with_copy_checks_the_new_value_and_keeps_the_others()
    {
        var policy = new IngestPublishPolicy.Adaptive { BatchCap = 500, StrictMaxStaleness = true };

        var tweaked = policy with { MaxStaleness = TimeSpan.FromMilliseconds(250) };

        await Assert.That(tweaked.BatchCap).IsEqualTo(500);
        await Assert.That(tweaked.StrictMaxStaleness).IsTrue();
        await Assert.That(tweaked.MaxStaleness).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(policy.MaxStaleness).IsEqualTo(TimeSpan.FromSeconds(1));
        await AssertOutOfRange("BatchCap", () => _ = policy with { BatchCap = -1 });
    }

    [Test]
    public async Task The_defaults_are_the_documented_ones()
    {
        var options = new IngestPublishOptions();
        var adaptive = (IngestPublishPolicy.Adaptive)options.Policy;
        var debounce = new IngestPublishPolicy.TimeDebounce();
        var fixedBatch = new IngestPublishPolicy.FixedBatch();

        await Assert.That(options.ChannelCapacity).IsEqualTo(10_000);
        await Assert.That(options.FlushOnCancellation).IsTrue();
        await Assert.That(adaptive).IsEqualTo(new IngestPublishPolicy.Adaptive
        {
            BatchCap = 2_000,
            GrowthAwareBatchCap = true,
            MinInterval = TimeSpan.FromMilliseconds(100),
            PublishMultiplier = 2.0,
            MinTimerPublishBatch = 200,
            MaxStaleness = TimeSpan.FromSeconds(1),
            StrictMaxStaleness = false,
        });
        await Assert.That(debounce.MinInterval).IsEqualTo(TimeSpan.FromMilliseconds(100));
        await Assert.That(debounce.MaxStaleness).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(fixedBatch.BatchSize).IsEqualTo(2_000);
        await Assert.That(fixedBatch.MaxStaleness).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    private static async Task AssertOutOfRange(string paramName, Action call)
    {
        var exception = await Assert.That(call).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo(paramName);
    }
}
