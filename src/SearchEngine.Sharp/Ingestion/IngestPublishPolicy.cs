namespace SearchEngine.Ingestion;

/// <summary>
/// Decides when <see cref="ProgressiveIndexIngestion"/> publishes the buffered entries of a scan. Every policy also
/// publishes what is left when the scan ends.
/// </summary>
public enum IngestPublishPolicy
{
    /// <summary>
    /// Publishes after every entry. Meant only as a baseline for small collections: total rebuild cost grows with
    /// the square of the document count.
    /// </summary>
    PerEntry,

    /// <summary>
    /// Publishes when the buffer reaches <see cref="IngestPublishOptions.FixedBatchSize"/> entries, or, when the
    /// next entry arrives, if the oldest buffered entry is older than <see cref="IngestPublishOptions.MaxStaleness"/>.
    /// </summary>
    FixedBatch,

    /// <summary>
    /// Publishes when the buffer is not empty and <see cref="IngestPublishOptions.MinInterval"/> has passed since the
    /// last publish, or when the oldest buffered entry reaches <see cref="IngestPublishOptions.MaxStaleness"/>.
    /// </summary>
    TimeDebounce,

    /// <summary>
    /// Publishes when at least <see cref="IngestPublishOptions.MinTimerPublishBatch"/> entries are pending and the
    /// elapsed time since the last publish is at least <c>max(MinInterval, AdaptiveMultiplier × lastRebuildDuration)</c>.
    /// Also publishes when the buffer reaches the current batch cap (see
    /// <see cref="IngestPublishOptions.GrowthAwareBatchCap"/>) and when the oldest pending entry exceeds
    /// <see cref="IngestPublishOptions.MaxStaleness"/>.
    /// </summary>
    Adaptive,
}
