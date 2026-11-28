using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SearchEngine.Ingestion;

// The publisher: takes entries from the channel into a batch and publishes it when the schedule says so, when the
// scan ends, and, with a flush on cancellation, when the ingestion is cancelled.
internal sealed class PublishLoop(
    ChannelReader<BufferedEntry> reader,
    PublishSchedule schedule,
    BatchPublisher publisher,
    TimeProvider timeProvider)
{
    private readonly PendingBatch _batch = new();

    // One outstanding read survives timer wake-ups, so a timer publish never leaves an orphaned waiter behind.
    private Task<bool>? _pendingRead;

    public async Task RunAsync(bool flushOnCancellation, CancellationToken cancellationToken)
    {
        using var wakeTimer = new WakeTimer(timeProvider);
        try
        {
            await PublishUntilScanEndsAsync(wakeTimer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (flushOnCancellation)
                await FlushAfterCancellationAsync().ConfigureAwait(false);

            throw;
        }
    }

    // Reports end of input as false, also when the scanner failed: the channel then throws the scanner's exception
    // once it is drained, sometimes before its Completion task is complete. The failure surfaces from the scanner's
    // task; the publisher must still flush its batch first, also when the caller cancels meanwhile. Only the caller's
    // cancellation propagates.
    internal static async Task<bool> WaitForEntryAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PublishUntilScanEndsAsync(WakeTimer wakeTimer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (schedule.TimeUntilTimerPublish(_batch) == TimeSpan.Zero)
                publisher.Publish(_batch);

            if (reader.Completion.IsCompleted)
            {
                TakeAvailable();
                publisher.Publish(_batch);
                return;
            }

            if (schedule.IsBatchFull(_batch.Count))
                publisher.Publish(_batch);
            else if (await WaitForEntriesOrTimerAsync(wakeTimer, cancellationToken).ConfigureAwait(false))
                TakeAvailableUntilFull();
        }
    }

    // Waits for the scanner and, while a timer publish can become due, for the timer too. Returns true when entries
    // may be available. A firing before the publish is due is re-armed by the next pass of the loop (WakeTimer). It
    // runs on every wake-up, so its state machine comes from a pool.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> WaitForEntriesOrTimerAsync(WakeTimer wakeTimer, CancellationToken cancellationToken)
    {
        _pendingRead ??= WaitForEntryAsync(reader, cancellationToken);

        if (schedule.TimeUntilTimerPublish(_batch) is { } wait)
        {
            Task timer = wakeTimer.Arm(wait);
            if (await Task.WhenAny(_pendingRead, timer).ConfigureAwait(false) != _pendingRead)
                return false;

            wakeTimer.Disarm();
        }

        bool entriesAvailable = await _pendingRead.ConfigureAwait(false);
        _pendingRead = null;
        return entriesAvailable;
    }

    private async Task FlushAfterCancellationAsync()
    {
        // Publish what has arrived at once: the scanner may take long to observe its cancellation.
        TakeAvailable();
        publisher.Publish(_batch);

        // The scanner is cancelled too. It may still hand over entries before it stops; read until it has
        // completed the channel so every accepted entry is published.
        while (await WaitForEntryAsync(reader, CancellationToken.None).ConfigureAwait(false))
            TakeAvailable();

        publisher.Publish(_batch);
    }

    private void TakeAvailable()
    {
        while (reader.TryRead(out BufferedEntry entry))
            _batch.Add(entry);
    }

    private void TakeAvailableUntilFull()
    {
        while (reader.TryRead(out BufferedEntry entry))
        {
            _batch.Add(entry);
            if (schedule.IsBatchFull(_batch.Count))
            {
                publisher.Publish(_batch);
                return;
            }
        }
    }
}
