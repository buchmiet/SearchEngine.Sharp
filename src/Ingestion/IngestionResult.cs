namespace SearchEngine.Ingestion;

/// <summary>
/// The outcome of a completed or cancelled ingestion of a <see cref="ProgressiveIndexIngestion"/>.
/// </summary>
public sealed class IngestionResult
{
    /// <summary>
    /// Gets the number of entries accepted from the scan.
    /// </summary>
    public required int EntriesAccepted { get; init; }

    /// <summary>
    /// Gets the number of entries published to the index; equals <see cref="EntriesAccepted"/> on successful
    /// completion.
    /// </summary>
    public required int EntriesPublished { get; init; }

    /// <summary>
    /// Gets the number of publishes.
    /// </summary>
    public required int PublishCount { get; init; }

    /// <summary>
    /// Gets the total wall-clock time spent inside the updater calls of the publishes: the sum of
    /// <see cref="IngestionPublishEvent.PublishDuration"/> over all publishes.
    /// </summary>
    public required TimeSpan TotalPublishDuration { get; init; }

    /// <summary>
    /// Gets the longest observed time between the scan handing an entry over and the start of the publish that
    /// makes it searchable. That publish's own duration (<see cref="IngestionPublishEvent.PublishDuration"/>) comes
    /// on top.
    /// </summary>
    public required TimeSpan MaxObservedStaleness { get; init; }

    /// <summary>
    /// Gets whether the ingestion ended because the scan ended, without cancellation.
    /// </summary>
    public required bool CompletedSuccessfully { get; init; }

    /// <summary>
    /// Gets whether the ingestion ended because it was cancelled. Always the opposite of
    /// <see cref="CompletedSuccessfully"/>: a failed ingestion throws instead of returning a result.
    /// </summary>
    public required bool WasCancelled { get; init; }
}
