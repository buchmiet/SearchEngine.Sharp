namespace SearchEngine.Ingestion;

/// <summary>
/// Applies a publish policy: decides whether the batch is full and how long until a timer publish is due. It
/// remembers when the last publish finished, how long it took and the current batch cap.
/// </summary>
internal sealed class PublishSchedule
{
    private readonly IngestPublishPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private long _lastPublishTimestamp;
    private TimeSpan _lastPublishDuration;
    private int _batchCap;

    public PublishSchedule(IngestPublishPolicy policy, TimeProvider timeProvider)
    {
        _policy = policy;
        _timeProvider = timeProvider;
        _lastPublishTimestamp = timeProvider.GetTimestamp();
        _batchCap = policy switch
        {
            IngestPublishPolicy.Adaptive adaptive => adaptive.BatchCap,
            IngestPublishPolicy.FixedBatch fixedBatch => fixedBatch.BatchSize,
            IngestPublishPolicy.PerEntry => 1,
            IngestPublishPolicy.TimeDebounce => int.MaxValue, // never publishes by size
        };
    }

    /// <summary>Whether <paramref name="count"/> buffered entries start a publish on their own.</summary>
    public bool IsBatchFull(int count) => count > 0 && count >= _batchCap;

    /// <summary>
    /// Records a finished publish. Under <see cref="IngestPublishPolicy.Adaptive"/> with a growing cap, the cap
    /// becomes <c>max(BatchCap, indexedCount)</c>.
    /// </summary>
    public void RecordPublish(TimeSpan publishDuration, int indexedCount)
    {
        _lastPublishDuration = publishDuration;
        _lastPublishTimestamp = _timeProvider.GetTimestamp();
        if (_policy is IngestPublishPolicy.Adaptive { GrowthAwareBatchCap: true } adaptive)
            _batchCap = Math.Max(adaptive.BatchCap, indexedCount);
    }

    /// <summary>
    /// Time until a timer-driven publish of <paramref name="batch"/> is due: <see cref="TimeSpan.Zero"/> when it is
    /// due now, <see langword="null"/> when no timer should be armed (nothing buffered, or a policy without a timer
    /// whose batch is not yet stale).
    /// </summary>
    public TimeSpan? TimeUntilTimerPublish(PendingBatch batch)
    {
        if (batch.Count == 0 || MaxStaleness() is not { } maxStaleness)
            return null;

        TimeSpan sinceLastPublish = _timeProvider.GetElapsedTime(_lastPublishTimestamp);
        TimeSpan untilStale = maxStaleness - _timeProvider.GetElapsedTime(batch.OldestTimestamp);

        // Once one publish takes longer than MaxStaleness, every batch is already stale when the previous publish
        // finishes; publishing on staleness alone then rebuilds the index back to back every ChannelCapacity entries
        // and total work becomes quadratic. Under Adaptive, staleness may not publish sooner than PublishMultiplier x
        // the last publish duration after that publish.
        if (_policy is IngestPublishPolicy.Adaptive { StrictMaxStaleness: false } paced)
            untilStale = Max(untilStale, _lastPublishDuration * paced.PublishMultiplier - sinceLastPublish);

        if (untilStale <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return _policy switch
        {
            IngestPublishPolicy.TimeDebounce debounce => EarlierOf(untilStale, debounce.MinInterval - sinceLastPublish),
            IngestPublishPolicy.Adaptive adaptive when batch.Count >= adaptive.MinTimerPublishBatch => EarlierOf(
                untilStale,
                Max(adaptive.MinInterval, _lastPublishDuration * adaptive.PublishMultiplier) - sinceLastPublish),
            IngestPublishPolicy.Adaptive => untilStale,
            IngestPublishPolicy.FixedBatch or IngestPublishPolicy.PerEntry => null, // no timer
        };
    }

    private TimeSpan? MaxStaleness() => _policy switch
    {
        IngestPublishPolicy.Adaptive adaptive => adaptive.MaxStaleness,
        IngestPublishPolicy.TimeDebounce debounce => debounce.MaxStaleness,
        IngestPublishPolicy.FixedBatch fixedBatch => fixedBatch.MaxStaleness,
        IngestPublishPolicy.PerEntry => null, // publishes every entry at once
    };

    private static TimeSpan EarlierOf(TimeSpan untilStale, TimeSpan untilPace)
        => Max(TimeSpan.Zero, untilStale <= untilPace ? untilStale : untilPace);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
