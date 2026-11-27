namespace SearchEngine.Ingestion;

/// <summary>
/// Outcome of a completed or cancelled <c>ProgressiveIndexIngestion.IngestAsync</c> call.
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
    /// Total wall-clock time spent inside index rebuilds: the sum of
    /// <see cref="IngestionPublishEvent.RebuildDuration"/> over all publishes.
    /// </summary>
    public required TimeSpan TotalRebuildDuration { get; init; }

    /// <summary>
    /// Longest observed delay between the scanner handing an entry over and the start of the publish
    /// that makes it searchable (that publish's rebuild, <see cref="IngestionPublishEvent.RebuildDuration"/>,
    /// comes on top).
    /// </summary>
    public required TimeSpan MaxObservedStaleness { get; init; }

    /// <summary>
    /// <see langword="true"/> when the scan completed without cancellation.
    /// </summary>
    public required bool CompletedSuccessfully { get; init; }

    /// <summary>
    /// <see langword="true"/> when the operation ended due to cancellation.
    /// </summary>
    public required bool WasCancelled { get; init; }
}
