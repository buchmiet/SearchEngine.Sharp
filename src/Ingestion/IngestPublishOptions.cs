namespace SearchEngine.Ingestion;

/// <summary>
/// Settings for <see cref="ProgressiveIndexIngestion"/>: the publish policy and how the scan is connected to the
/// publisher. Every setting is checked when it is set; change settings with an object initializer or a
/// <see langword="with"/> expression.
/// </summary>
public sealed record class IngestPublishOptions
{
    /// <summary>
    /// Gets the policy that decides when buffered entries are published, with its own settings. Defaults to an
    /// <see cref="IngestPublishPolicy.Adaptive"/> policy with its default settings.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IngestPublishPolicy Policy
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Policy));
            field = value;
        }
    } = new IngestPublishPolicy.Adaptive();

    /// <summary>
    /// Gets the capacity of the channel between the scan and the publisher. When it is full, the scan waits until
    /// the publisher takes entries from it. Must be at least 1. Defaults to 10,000.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int ChannelCapacity
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(ChannelCapacity));
            field = value;
        }
    } = 10_000;

    /// <summary>
    /// Gets whether a cancelled ingestion publishes the accepted entries that are not yet published, including those
    /// still in the channel, before it completes. When <see langword="false"/>, they are discarded. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool FlushOnCancellation { get; init; } = true;
}
