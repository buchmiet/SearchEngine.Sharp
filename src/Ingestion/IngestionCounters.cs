namespace SearchEngine.Ingestion;

// The counters of one ingestion. The scanner counts accepted entries; the publisher counts everything else and reads
// the accepted count for its progress reports. Only EntriesAccepted is shared with the scanner; the publisher writes
// the others, and they are read on its task or after it has finished, so they take no lock.
internal sealed class IngestionCounters(TimeProvider timeProvider)
{
    private int _entriesAccepted;

    public int EntriesAccepted => Volatile.Read(ref _entriesAccepted);
    public int EntriesPublished { get; private set; }
    public int PublishCount { get; private set; }
    public TimeSpan TotalPublishDuration { get; private set; }
    public TimeSpan MaxObservedStaleness { get; private set; }

    public void IncrementAccepted() => Interlocked.Increment(ref _entriesAccepted);

    public void DecrementAccepted() => Interlocked.Decrement(ref _entriesAccepted);

    public void AddPublished(int count, TimeSpan publishDuration)
    {
        EntriesPublished += count;
        PublishCount++;
        TotalPublishDuration += publishDuration;
    }

    public void ObserveStaleness(long oldestTimestamp, long publishStarted)
    {
        TimeSpan staleness = timeProvider.GetElapsedTime(oldestTimestamp, publishStarted);
        if (staleness > MaxObservedStaleness)
            MaxObservedStaleness = staleness;
    }

    public IngestionProgress ToProgress()
    {
        int accepted = EntriesAccepted;
        return new()
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
        TotalPublishDuration = TotalPublishDuration,
        MaxObservedStaleness = MaxObservedStaleness,
        CompletedSuccessfully = completed,
        WasCancelled = cancelled,
    };
}
