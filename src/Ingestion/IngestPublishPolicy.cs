namespace SearchEngine.Ingestion;

/// <summary>
/// Decides when <see cref="ProgressiveIndexIngestion"/> publishes the buffered entries of a scan. Each policy is a
/// record that carries only its own settings, with defaults; change them with an object initializer or a
/// <see langword="with"/> expression. Every setting is checked when it is set, so an invalid value throws
/// <see cref="ArgumentOutOfRangeException"/> naming the property. Every policy also publishes what is left when the
/// scan ends.
/// </summary>
/// <remarks>
/// <para>The policies are <see cref="Adaptive"/> (the default), <see cref="TimeDebounce"/>, <see cref="FixedBatch"/>
/// and <see cref="PerEntry"/>. The hierarchy is closed: no other assembly can add a policy, so a
/// <see langword="switch"/> expression over the four cases is exhaustive.</para>
/// <para>Which settings each policy has:</para>
/// <list type="table">
///   <listheader><term>Setting</term><description>Policies</description></listheader>
///   <item><term>Batch size or batch cap</term><description><see cref="Adaptive.BatchCap"/>,
///         <see cref="FixedBatch.BatchSize"/></description></item>
///   <item><term><c>GrowthAwareBatchCap</c>, <c>RebuildMultiplier</c>, <c>MinTimerPublishBatch</c>,
///         <c>StrictMaxStaleness</c></term><description><see cref="Adaptive"/></description></item>
///   <item><term><c>MinInterval</c></term><description><see cref="Adaptive.MinInterval"/>,
///         <see cref="TimeDebounce.MinInterval"/></description></item>
///   <item><term><c>MaxStaleness</c></term><description><see cref="Adaptive.MaxStaleness"/> and
///         <see cref="TimeDebounce.MaxStaleness"/> (enforced by a timer), <see cref="FixedBatch.MaxStaleness"/>
///         (checked only when the next entry arrives)</description></item>
///   <item><term>none</term><description><see cref="PerEntry"/></description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var adaptive = new IngestPublishPolicy.Adaptive { BatchCap = 500 };
/// var options = new IngestPublishOptions { Policy = adaptive with { MaxStaleness = TimeSpan.FromMilliseconds(250) } };
/// </code>
/// </example>
public closed record class IngestPublishPolicy
{
    private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultMaxStaleness = TimeSpan.FromSeconds(1);
    private const int DefaultBatchSize = 2_000;

    /// <summary>
    /// Publishes when the buffer reaches the batch cap, when at least <see cref="MinTimerPublishBatch"/> entries wait
    /// and <c>max(MinInterval, RebuildMultiplier × last rebuild duration)</c> has passed since the last publish, or
    /// when the oldest buffered entry has waited <see cref="MaxStaleness"/>. A timer makes the time-based publishes
    /// happen while the scanner is idle. Suits scans of any speed and size.
    /// </summary>
    public sealed record class Adaptive : IngestPublishPolicy
    {
        /// <summary>
        /// Gets the batch cap at the start: a publish happens when this many entries are buffered. With
        /// <see cref="GrowthAwareBatchCap"/> it is also the lower bound of the growing cap. Must be at least 1.
        /// Defaults to 2,000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int BatchCap
        {
            get;
            init => field = RequireAtLeastOne(value, nameof(BatchCap));
        } = DefaultBatchSize;

        /// <summary>
        /// Gets whether the batch cap grows with the index. When <see langword="true"/> (the default), the cap
        /// becomes, after each publish, <c>max(BatchCap, number of entries the updater holds)</c>, so the number of
        /// full rebuilds stays low as the index grows. When <see langword="false"/>, the cap stays at
        /// <see cref="BatchCap"/>.
        /// </summary>
        public bool GrowthAwareBatchCap { get; init; } = true;

        /// <summary>
        /// Gets the shortest time between timer publishes. A publish caused by <see cref="MaxStaleness"/> or by a
        /// full batch does not wait for it. Must not be negative. Defaults to 100 ms.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public TimeSpan MinInterval
        {
            get;
            init => field = RequireNotNegative(value, nameof(MinInterval));
        } = DefaultMinInterval;

        /// <summary>
        /// The largest <see cref="RebuildMultiplier"/>: 1,000. At that value the publisher spends about 0.1% of its
        /// time rebuilding while the timer drives the publishes, so a larger value saves nothing measurable; it only
        /// delays results. The limit also keeps <c>k × last rebuild duration</c> inside the range of
        /// <see cref="TimeSpan"/> for any rebuild shorter than 29 years.
        /// </summary>
        public const double MaxRebuildMultiplier = 1_000;

        /// <summary>
        /// Gets the multiplier <c>k</c> of the interval <c>max(MinInterval, k × last rebuild duration)</c> between
        /// timer publishes. It also paces staleness publishes (see <see cref="MaxStaleness"/>). Must be positive and
        /// at most <see cref="MaxRebuildMultiplier"/>. Defaults to 2.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative, NaN or larger than
        /// <see cref="MaxRebuildMultiplier"/>.</exception>
        public double RebuildMultiplier
        {
            get;
            init
            {
                if (!(value is > 0 and <= MaxRebuildMultiplier))
                    throw new ArgumentOutOfRangeException(nameof(RebuildMultiplier), value, $"Must be positive and at most {MaxRebuildMultiplier}.");

                field = value;
            }
        } = 2.0;

        /// <summary>
        /// Gets the fewest buffered entries for a timer publish. A publish caused by <see cref="MaxStaleness"/>
        /// ignores it. Must be at least 1. Defaults to 200.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int MinTimerPublishBatch
        {
            get;
            init => field = RequireAtLeastOne(value, nameof(MinTimerPublishBatch));
        } = 200;

        /// <summary>
        /// Gets the longest time an accepted entry should wait before the publish that contains it starts. A timer
        /// enforces it while the scanner is idle. Must be positive. Defaults to 1 second.
        /// </summary>
        /// <remarks>
        /// <para>Staleness is measured from the moment the scanner hands the entry over until the publish that
        /// contains it starts (see <see cref="IngestionResult.MaxObservedStaleness"/>); that publish's own rebuild
        /// comes on top, as does time spent in the <c>Published</c> and <c>onPublished</c> handlers.</para>
        /// <para>Unless <see cref="StrictMaxStaleness"/> is set, a staleness publish also waits until
        /// <see cref="RebuildMultiplier"/> × the last rebuild duration has passed since the previous publish
        /// finished. An entry handed over just as a rebuild starts therefore waits that rebuild plus the gap, so
        /// with <c>k = RebuildMultiplier</c> and a rebuild of duration <c>R</c> the worst staleness is
        /// <c>max(MaxStaleness, (1 + k) × R)</c>. While <c>R</c> stays below <c>MaxStaleness / (1 + k)</c> (333 ms
        /// with the defaults) the bound is <see cref="MaxStaleness"/>; beyond that it grows with the index (a 400 ms
        /// rebuild gives 1.2 s instead of 1.0 s), which keeps the scan from running back-to-back full
        /// rebuilds.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
        public TimeSpan MaxStaleness
        {
            get;
            init => field = RequirePositive(value, nameof(MaxStaleness));
        } = DefaultMaxStaleness;

        /// <summary>
        /// Gets whether a publish starts as soon as the oldest buffered entry exceeds <see cref="MaxStaleness"/>,
        /// even when that means starting a full rebuild immediately after the previous one. Once a single rebuild
        /// takes longer than <see cref="MaxStaleness"/>, every batch is stale when the previous rebuild ends, so with
        /// this set rebuilds run back to back and total rebuild work grows with the square of the index size.
        /// Defaults to <see langword="false"/>, which applies the pacing described under
        /// <see cref="MaxStaleness"/>.
        /// </summary>
        public bool StrictMaxStaleness { get; init; }
    }

    /// <summary>
    /// Publishes when the buffer is not empty and <see cref="MinInterval"/> has passed since the last publish, or
    /// when the oldest buffered entry has waited <see cref="MaxStaleness"/>. A timer makes both happen while the
    /// scanner is idle. It never publishes because of the number of buffered entries. Suits scans that should
    /// refresh at a fixed rate.
    /// </summary>
    public sealed record class TimeDebounce : IngestPublishPolicy
    {
        /// <summary>
        /// Gets the shortest time between publishes. A publish caused by <see cref="MaxStaleness"/> does not wait
        /// for it. Must not be negative. Defaults to 100 ms.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public TimeSpan MinInterval
        {
            get;
            init => field = RequireNotNegative(value, nameof(MinInterval));
        } = DefaultMinInterval;

        /// <summary>
        /// Gets the longest time an accepted entry should wait before the publish that contains it starts. A timer
        /// enforces it while the scanner is idle. It matters only when it is shorter than <see cref="MinInterval"/>
        /// or a rebuild is long: an entry that arrives during a rebuild is older than the time since that publish.
        /// Must be positive. Defaults to 1 second.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
        public TimeSpan MaxStaleness
        {
            get;
            init => field = RequirePositive(value, nameof(MaxStaleness));
        } = DefaultMaxStaleness;
    }

    /// <summary>
    /// Publishes when the buffer reaches <see cref="BatchSize"/> entries, or, when the next entry arrives, if the
    /// oldest buffered entry has waited <see cref="MaxStaleness"/>. It arms no timer, so an idle scanner leaves
    /// entries unpublished until it hands over the next entry or ends. Suits tests and benchmarks.
    /// </summary>
    public sealed record class FixedBatch : IngestPublishPolicy
    {
        /// <summary>
        /// Gets the number of buffered entries that starts a publish. Must be at least 1. Defaults to 2,000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int BatchSize
        {
            get;
            init => field = RequireAtLeastOne(value, nameof(BatchSize));
        } = DefaultBatchSize;

        /// <summary>
        /// Gets the age of the oldest buffered entry beyond which the next arriving entry starts a publish. It is
        /// checked only when an entry arrives, never from a timer. Must be positive; <see cref="TimeSpan.MaxValue"/>
        /// turns it off. Defaults to 1 second.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
        public TimeSpan MaxStaleness
        {
            get;
            init => field = RequirePositive(value, nameof(MaxStaleness));
        } = DefaultMaxStaleness;
    }

    /// <summary>
    /// Publishes after every entry. It has no settings. Meant only as a baseline for small collections: total
    /// rebuild cost grows with the square of the entry count.
    /// </summary>
    public sealed record class PerEntry : IngestPublishPolicy;

    private static int RequireAtLeastOne(int value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, paramName);
        return value;
    }

    private static TimeSpan RequireNotNegative(TimeSpan value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, paramName);
        return value;
    }

    private static TimeSpan RequirePositive(TimeSpan value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, paramName);
        return value;
    }
}
