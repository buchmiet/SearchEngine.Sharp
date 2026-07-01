namespace SearchEngine.Ingestion;

/// <summary>
/// Raised after each successful index publish during ingestion.
/// </summary>
public readonly struct IngestionPublishEvent
{
    /// <summary>
    /// Entries included in this publish.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Total indexed document count after this publish.
    /// </summary>
    public int IndexedDocumentCount { get; init; }

    /// <summary>
    /// Wall-clock time spent rebuilding the index for this publish.
    /// </summary>
    public TimeSpan RebuildDuration { get; init; }

    /// <summary>
    /// Zero-based publish sequence number within the ingestion.
    /// </summary>
    public int PublishSequence { get; init; }
}
