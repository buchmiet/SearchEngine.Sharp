using System.Threading.Channels;

namespace SearchEngine.Ingestion;

/// <summary>
/// Reads the scan, turns each item into an <see cref="IndexedEntry"/> stamped with the time of the handover and
/// writes it into the channel. Completes the channel when the scan ends, with the scan's exception when it fails.
/// </summary>
internal sealed class ScanProducer<TEntry>(
    IAsyncEnumerable<KeyValuePair<int, TEntry>> entries,
    Func<KeyValuePair<int, TEntry>, IndexedEntry> toIndexedEntry,
    TimeProvider timeProvider)
{
    public async Task RunAsync(
        ChannelWriter<BufferedEntry> writer,
        IngestionCounters counters,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pair in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var buffered = new BufferedEntry(pair.Key, toIndexedEntry(pair), timeProvider.GetTimestamp());
                await WriteAcceptedAsync(writer, buffered, counters, cancellationToken).ConfigureAwait(false);
            }

            writer.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    // An entry is counted as accepted before it enters the channel, so the publisher can never publish an entry
    // that is not counted yet. The count is taken back when the channel is full; the scanner then waits for room.
    private static async ValueTask WriteAcceptedAsync(
        ChannelWriter<BufferedEntry> writer,
        BufferedEntry entry,
        IngestionCounters counters,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // Like ChannelWriter.WriteAsync: nothing is accepted once the ingestion is cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            counters.IncrementAccepted();
            if (writer.TryWrite(entry))
                return;

            counters.DecrementAccepted();
            if (!await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                throw new ChannelClosedException();
        }
    }
}
