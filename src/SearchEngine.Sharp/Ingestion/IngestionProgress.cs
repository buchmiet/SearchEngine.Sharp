namespace SearchEngine.Ingestion;

/// <summary>
/// Progress notification emitted while an ingestion is running.
/// </summary>
public readonly struct IngestionProgress
{
    /// <summary>
    /// Entries accepted from the scan source so far (including the current buffer).
    /// </summary>
    public int EntriesAccepted { get; init; }

    /// <summary>
    /// Entries published to the index so far.
    /// </summary>
    public int EntriesPublished { get; init; }

    /// <summary>
    /// Number of index publishes completed so far.
    /// </summary>
    public int PublishCount { get; init; }

    /// <summary>
    /// Entries still waiting in the publisher buffer.
    /// </summary>
    public int PendingCount { get; init; }
}
