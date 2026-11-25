namespace SearchEngine.Ingestion;

/// <summary>
/// The counters of one ingestion. The scanner counts accepted entries; the publisher counts everything else and
/// reads the accepted count for its progress reports.
/// </summary>
internal sealed class IngestionCounters(TimeProvider timeProvider)
{
    private int _entriesAccepted;

    public int EntriesAccepted => Volatile.Read(ref _entriesAccepted);
    public int EntriesPublished { get; private set; }
    public int PublishCount { get; private set; }
    public TimeSpan TotalRebuildDuration { get; private set; }
    public TimeSpan MaxObservedStaleness { get; private set; }

    public void IncrementAccepted() => Interlocked.Increment(ref _entriesAccepted);

    public void DecrementAccepted() => Interlocked.Decrement(ref _entriesAccepted);

    public void AddPublished(int count, TimeSpan rebuildDuration)
    {
        EntriesPublished += count;
        PublishCount++;
        TotalRebuildDuration += rebuildDuration;
    }

    // Only the publisher calls this, and the result is read after the publisher has finished.
    public void ObserveStaleness(long oldestTimestamp, long publishStarted)
    {
        TimeSpan staleness = timeProvider.GetElapsedTime(oldestTimestamp, publishStarted);
        if (staleness > MaxObservedStaleness)
            MaxObservedStaleness = staleness;
    }

    public IngestionProgress ToProgress()
    {
        int accepted = EntriesAccepted;
        return new IngestionProgress
        {
            EntriesAccepted = accepted,
            EntriesPublished = EntriesPublished,
            PublishCount = PublishCount,
            PendingCount = accepted - EntriesPublished,
        };
    }

    public IngestionResult ToResult(bool completed, bool cancelled) => new()
    {
        EntriesAccepted = EntriesAccepted,
        EntriesPublished = EntriesPublished,
        PublishCount = PublishCount,
        TotalRebuildDuration = TotalRebuildDuration,
        MaxObservedStaleness = MaxObservedStaleness,
        CompletedSuccessfully = completed,
        WasCancelled = cancelled,
    };
}
