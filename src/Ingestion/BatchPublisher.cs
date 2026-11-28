namespace SearchEngine.Ingestion;

// Who hears about a publish: the Published event, the onPublished callback and the progress receiver, in that order.
internal readonly record struct PublishObservers(
    Action<IngestionPublishEvent> RaisePublished,
    Action<IngestionPublishEvent>? OnPublished,
    IProgress<IngestionProgress>? Progress);

// Publishes a batch to the updater, records the publish in the schedule and the counters, empties the batch and then
// tells the observers.
internal sealed class BatchPublisher(
    IIndexUpdater updater,
    TimeProvider timeProvider,
    PublishSchedule schedule,
    IngestionCounters counters,
    PublishObservers observers)
{
    // Does nothing when the batch is empty.
    public void Publish(PendingBatch batch)
    {
        if (batch.Count == 0)
            return;

        long publishStarted = timeProvider.GetTimestamp();
        counters.ObserveStaleness(batch.OldestTimestamp, publishStarted);

        updater.AddOrUpdateEntries(batch.Entries);

        TimeSpan publishDuration = timeProvider.GetElapsedTime(publishStarted);
        int indexedCount = updater.EntryCount;
        schedule.RecordPublish(publishDuration, indexedCount);

        // The batch is in the index: count it and empty the batch before any handler runs, so a handler that
        // throws (for example after the caller cancelled) cannot make the flush publish the same entries again.
        var publishEvent = new IngestionPublishEvent
        {
            EntryCount = batch.Count,
            IndexedDocumentCount = indexedCount,
            PublishDuration = publishDuration,
            PublishSequence = counters.PublishCount,
        };
        counters.AddPublished(batch.Count, publishDuration);
        batch.Clear();

        observers.RaisePublished(publishEvent);
        observers.OnPublished?.Invoke(publishEvent);
        observers.Progress?.Report(counters.ToProgress());
    }
}
