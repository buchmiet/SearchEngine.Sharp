namespace SearchEngine.Ingestion;

/// <summary>
/// The counters of a running ingestion, reported after each publish.
/// </summary>
public readonly struct IngestionProgress
{
    /// <summary>
    /// Gets the number of entries taken from the scan so far, including those not yet published.
    /// </summary>
    public int EntriesAccepted { get; init; }

    /// <summary>
    /// Gets the number of entries published so far. An id published twice counts twice.
    /// </summary>
    public int EntriesPublished { get; init; }

    /// <summary>
    /// Gets the number of publishes so far.
    /// </summary>
    public int PublishCount { get; init; }

    /// <summary>
    /// Gets the number of entries accepted but not yet published when this report was made:
    /// <see cref="EntriesAccepted"/> minus <see cref="EntriesPublished"/>. These are the entries queued behind the
    /// publish just finished, in the channel or in the publisher's buffer. An entry is counted as accepted before
    /// the publisher can take it, so the value is never negative.
    /// </summary>
    public int PendingCount { get; init; }
}
