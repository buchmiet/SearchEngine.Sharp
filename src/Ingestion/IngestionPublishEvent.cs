namespace SearchEngine.Ingestion;

/// <summary>
/// Describes one publish of a <see cref="ProgressiveIndexIngestion"/>. Passed to
/// <see cref="ProgressiveIndexIngestion.Published"/> and to the <c>onPublished</c> callback.
/// </summary>
public readonly struct IngestionPublishEvent
{
    /// <summary>
    /// Gets the number of entries in this publish.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Gets the number of entries the updater holds after this publish, including entries it held before the
    /// ingestion started.
    /// </summary>
    public int IndexedDocumentCount { get; init; }

    /// <summary>
    /// Gets the elapsed time of the updater call for this publish, which consists mostly of building the snapshot.
    /// </summary>
    public TimeSpan PublishDuration { get; init; }

    /// <summary>
    /// Gets the zero-based number of this publish within the ingestion.
    /// </summary>
    public int PublishSequence { get; init; }
}
