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
    /// Entries accepted but not yet published when this report was made. Progress is reported after each
    /// publish, so these are the entries queued behind it (<see cref="EntriesAccepted"/> minus
    /// <see cref="EntriesPublished"/>, never below zero).
    /// </summary>
    public int PendingCount { get; init; }
}
