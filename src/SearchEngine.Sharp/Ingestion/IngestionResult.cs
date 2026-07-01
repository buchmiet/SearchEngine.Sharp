namespace SearchEngine.Ingestion;

/// <summary>
/// Outcome of a completed or cancelled <see cref="ProgressiveIndexIngestion.IngestAsync"/> call.
/// </summary>
public sealed class IngestionResult
{
    /// <summary>
    /// Entries accepted from the scan source.
    /// </summary>
    public required int EntriesAccepted { get; init; }

    /// <summary>
    /// Entries published to the index (equals <see cref="EntriesAccepted"/> on successful completion).
    /// </summary>
    public required int EntriesPublished { get; init; }

    /// <summary>
    /// Number of index publishes performed.
    /// </summary>
    public required int PublishCount { get; init; }

    /// <summary>
    /// Total wall-clock time spent inside index rebuilds.
    /// </summary>
    public required TimeSpan TotalRebuildCpu { get; init; }

    /// <summary>
    /// Longest observed delay between an entry entering the buffer and becoming searchable.
    /// </summary>
    public required TimeSpan WorstCaseStaleness { get; init; }

    /// <summary>
    /// <see langword="true"/> when the scan completed without cancellation.
    /// </summary>
    public required bool CompletedSuccessfully { get; init; }

    /// <summary>
    /// <see langword="true"/> when the operation ended due to cancellation.
    /// </summary>
    public required bool WasCancelled { get; init; }
}
