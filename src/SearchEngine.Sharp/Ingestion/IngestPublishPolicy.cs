namespace SearchEngine.Ingestion;

/// <summary>
/// Controls when buffered scan results are published to the search index.
/// </summary>
public enum IngestPublishPolicy
{
    /// <summary>
    /// Publishes after every entry. Intended only as a baseline for small collections;
    /// total rebuild cost grows quadratically with document count.
    /// </summary>
    PerEntry,

    /// <summary>
    /// Publishes when the pending buffer reaches <see cref="IngestPublishOptions.FixedBatchSize"/>.
    /// </summary>
    FixedBatch,

    /// <summary>
    /// Publishes when the pending buffer is non-empty and
    /// <see cref="IngestPublishOptions.MinInterval"/> has elapsed since the last publish.
    /// </summary>
    TimeDebounce,

    /// <summary>
    /// Publishes when the pending buffer is non-empty and elapsed time since the last publish
    /// is at least <c>max(MinInterval, AdaptiveMultiplier × lastRebuildDuration)</c>.
    /// Also publishes when the buffer reaches <see cref="IngestPublishOptions.FixedBatchSize"/>.
    /// </summary>
    Adaptive,
}
