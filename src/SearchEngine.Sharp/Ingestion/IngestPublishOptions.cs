namespace SearchEngine.Ingestion;

/// <summary>
/// Tunables for <see cref="ProgressiveIndexIngestion"/>.
/// </summary>
public sealed class IngestPublishOptions
{
    /// <summary>
    /// Publish policy. Defaults to <see cref="IngestPublishPolicy.Adaptive"/>.
    /// </summary>
    public IngestPublishPolicy Policy { get; init; } = IngestPublishPolicy.Adaptive;

    /// <summary>
    /// Maximum pending entries before a publish is forced (all policies except
    /// <see cref="IngestPublishPolicy.PerEntry"/>). Defaults to 2,000.
    /// </summary>
    public int FixedBatchSize { get; init; } = 2_000;

    /// <summary>
    /// Minimum delay between publishes for <see cref="IngestPublishPolicy.TimeDebounce"/>
    /// and the floor for <see cref="IngestPublishPolicy.Adaptive"/>. Defaults to 100 ms.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Multiplier <c>k</c> in the adaptive interval
    /// <c>max(MinInterval, k × lastRebuildDuration)</c>. Defaults to 2.
    /// </summary>
    public double AdaptiveMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Bounded channel capacity between the scanner and publisher. When full, the scanner
    /// awaits until the publisher drains entries (backpressure). Defaults to 10,000.
    /// </summary>
    public int ChannelCapacity { get; init; } = 10_000;

    /// <summary>
    /// When <see langword="true"/> (default), a cancelled ingestion flushes any buffered
    /// entries before completing. When <see langword="false"/>, buffered entries are discarded.
    /// </summary>
    public bool FlushOnCancellation { get; init; } = true;

    /// <summary>
    /// Maximum time an accepted entry may remain unpublished before a staleness-triggered
    /// publish is forced. Defaults to 1 second.
    /// </summary>
    /// <remarks>
    /// <para>Staleness is measured from the moment the scanner hands the entry over until the publish
    /// that contains it starts (see <see cref="IngestionResult.WorstCaseStaleness"/>); that publish's own
    /// rebuild comes on top, as does time spent in the <c>Published</c> and <c>onPublished</c> handlers.</para>
    /// <para>Under <see cref="IngestPublishPolicy.Adaptive"/> (unless <see cref="StrictMaxStaleness"/> is set)
    /// a staleness-triggered publish also waits until <see cref="AdaptiveMultiplier"/> × the last rebuild
    /// duration has passed since the previous publish finished. An entry handed over just as a rebuild
    /// starts therefore waits that rebuild plus the gap, so with <c>k = AdaptiveMultiplier</c> and a rebuild
    /// of duration <c>R</c> the worst staleness is <c>max(MaxStaleness, (1 + k) × R)</c>. Nothing changes
    /// while <c>R</c> stays below <c>MaxStaleness / (1 + k)</c> (333 ms with the defaults); beyond that
    /// the bound grows with the index (a 400 ms rebuild gives 1.2 s instead of 1.0 s) instead of the scan
    /// degrading to back-to-back full rebuilds.</para>
    /// </remarks>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum buffered entries required for a timer-triggered publish under
    /// <see cref="IngestPublishPolicy.Adaptive"/> (staleness always overrides this).
    /// Defaults to 200.
    /// </summary>
    public int MinTimerPublishBatch { get; init; } = 200;

    /// <summary>
    /// When <see langword="true"/> (default) and <see cref="Policy"/> is
    /// <see cref="IngestPublishPolicy.Adaptive"/>, the batch cap grows after each publish to
    /// <c>max(FixedBatchSize, indexed document count)</c> so fast scans avoid fixed-size
    /// rebuild amplification.
    /// </summary>
    public bool GrowthAwareBatchCap { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, <see cref="IngestPublishPolicy.Adaptive"/> publishes as soon as the
    /// oldest buffered entry exceeds <see cref="MaxStaleness"/>, even when that means starting a full
    /// rebuild immediately after the previous one (0.5.6 behaviour; quadratic total rebuild work once a
    /// rebuild takes longer than <see cref="MaxStaleness"/>). Defaults to <see langword="false"/>.
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
