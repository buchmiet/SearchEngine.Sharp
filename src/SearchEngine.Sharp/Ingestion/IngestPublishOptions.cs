namespace SearchEngine.Ingestion;

/// <summary>
/// Settings for <see cref="ProgressiveIndexIngestion"/>. All properties are init-only; they are validated when
/// <c>IngestAsync</c> starts.
/// </summary>
public sealed class IngestPublishOptions
{
    /// <summary>
    /// Gets the publish policy. Defaults to <see cref="IngestPublishPolicy.Adaptive"/>.
    /// </summary>
    public IngestPublishPolicy Policy { get; init; } = IngestPublishPolicy.Adaptive;

    /// <summary>
    /// Gets the batch size for <see cref="IngestPublishPolicy.FixedBatch"/>: a publish happens when this many
    /// entries are pending. Under <see cref="IngestPublishPolicy.Adaptive"/> it is the initial batch cap and the
    /// lower bound of the cap (see <see cref="GrowthAwareBatchCap"/>). <see cref="IngestPublishPolicy.PerEntry"/> and
    /// <see cref="IngestPublishPolicy.TimeDebounce"/> ignore it. Must be at least 1. Defaults to 2,000.
    /// </summary>
    public int FixedBatchSize { get; init; } = 2_000;

    /// <summary>
    /// Gets the shortest time between timer publishes under <see cref="IngestPublishPolicy.TimeDebounce"/>, and the
    /// lower bound of the interval under <see cref="IngestPublishPolicy.Adaptive"/>. A publish caused by
    /// <see cref="MaxStaleness"/> or by a full batch does not wait for it. Must not be negative. Defaults to
    /// 100 ms.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the multiplier <c>k</c> of the adaptive interval <c>max(MinInterval, k × lastRebuildDuration)</c>,
    /// which also paces staleness publishes (see <see cref="MaxStaleness"/>). Must be positive. Defaults to 2.
    /// </summary>
    public double AdaptiveMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Gets the capacity of the channel between the scan and the publisher. When it is full, the scan waits until
    /// the publisher takes entries from it. Must be at least 1. Defaults to 10,000.
    /// </summary>
    public int ChannelCapacity { get; init; } = 10_000;

    /// <summary>
    /// Gets whether a cancelled ingestion publishes the accepted entries that are not yet published, including those
    /// still in the channel, before it completes. When <see langword="false"/>, they are discarded. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool FlushOnCancellation { get; init; } = true;

    /// <summary>
    /// Gets the longest time an accepted entry should wait before the publish that contains it starts. Must be
    /// positive. Defaults to 1 second.
    /// </summary>
    /// <remarks>
    /// <para>Under <see cref="IngestPublishPolicy.Adaptive"/> and <see cref="IngestPublishPolicy.TimeDebounce"/>
    /// a timer enforces it while the scanner is idle. <see cref="IngestPublishPolicy.FixedBatch"/> and
    /// <see cref="IngestPublishPolicy.PerEntry"/> arm no timer: a stale buffer is published when the next entry
    /// arrives or the scan ends, so an idle scanner can leave entries unpublished longer than this.</para>
    /// <para>Staleness is measured from the moment the scanner hands the entry over until the publish
    /// that contains it starts (see <see cref="IngestionResult.MaxObservedStaleness"/>); that publish's own
    /// rebuild comes on top, as does time spent in the <c>Published</c> and <c>onPublished</c> handlers.</para>
    /// <para>Under <see cref="IngestPublishPolicy.Adaptive"/> (unless <see cref="StrictMaxStaleness"/> is set)
    /// a staleness-triggered publish also waits until <see cref="AdaptiveMultiplier"/> × the last rebuild
    /// duration has passed since the previous publish finished. An entry handed over just as a rebuild
    /// starts therefore waits that rebuild plus the gap, so with <c>k = AdaptiveMultiplier</c> and a rebuild
    /// of duration <c>R</c> the worst staleness is <c>max(MaxStaleness, (1 + k) × R)</c>. While <c>R</c> stays
    /// below <c>MaxStaleness / (1 + k)</c> (333 ms with the defaults) the bound is <see cref="MaxStaleness"/>;
    /// beyond that it grows with the index (a 400 ms rebuild gives 1.2 s instead of 1.0 s), which keeps the scan
    /// from running back-to-back full rebuilds.</para>
    /// </remarks>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the fewest buffered entries for a timer publish under <see cref="IngestPublishPolicy.Adaptive"/>. A
    /// publish caused by <see cref="MaxStaleness"/> ignores it. Must be at least 1. Defaults to 200.
    /// </summary>
    public int MinTimerPublishBatch { get; init; } = 200;

    /// <summary>
    /// Gets whether the batch cap of <see cref="IngestPublishPolicy.Adaptive"/> grows with the index. When
    /// <see langword="true"/> (the default), the cap becomes, after each publish,
    /// <c>max(FixedBatchSize, number of entries the updater holds)</c>, so the number of full rebuilds stays low as
    /// the index grows. When <see langword="false"/>, the cap stays at <see cref="FixedBatchSize"/>.
    /// </summary>
    public bool GrowthAwareBatchCap { get; init; } = true;

    /// <summary>
    /// Gets whether <see cref="IngestPublishPolicy.Adaptive"/> publishes as soon as the
    /// oldest buffered entry exceeds <see cref="MaxStaleness"/>, even when that means starting a full
    /// rebuild immediately after the previous one. Once a single rebuild takes longer than
    /// <see cref="MaxStaleness"/>, every batch is stale when the previous rebuild ends, so with this set rebuilds run
    /// back to back and total rebuild work grows with the square of the index size. Defaults to
    /// <see langword="false"/>, which applies the pacing described under <see cref="MaxStaleness"/>.
    /// </summary>
    public bool StrictMaxStaleness { get; init; }

    internal void Validate()
    {
        if (FixedBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(FixedBatchSize), "Must be at least 1.");

        if (MinInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinInterval), "Must be non-negative.");

        if (MaxStaleness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxStaleness), "Must be positive.");

        if (MinTimerPublishBatch < 1)
            throw new ArgumentOutOfRangeException(nameof(MinTimerPublishBatch), "Must be at least 1.");

        if (AdaptiveMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveMultiplier), "Must be positive.");

        if (ChannelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(ChannelCapacity), "Must be at least 1.");
    }
}
