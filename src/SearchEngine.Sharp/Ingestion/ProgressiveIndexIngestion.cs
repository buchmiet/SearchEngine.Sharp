using System.Diagnostics;
using System.Threading.Channels;

namespace SearchEngine.Ingestion;

/// <summary>
/// Buffers scan results and publishes them to <see cref="IIndexUpdater"/> according to a
/// configurable policy so progressive search stays cheap during large directory scans.
/// </summary>
/// <remarks>
/// <para><b>Concurrency contract</b></para>
/// <list type="bullet">
///   <item>Only one <see cref="IngestAsync"/> call may run per instance at a time.</item>
///   <item>The scanner feeds a bounded channel; when the channel is full the scanner awaits
///         (backpressure) until the publisher drains entries.</item>
///   <item>Concurrent <see cref="ISearchEngine"/> queries against the same
///         <see cref="IIndexSnapshotProvider"/> are safe while ingestion runs.</item>
///   <item><see cref="Published"/> and <c>onPublished</c> callbacks run on the publisher task,
///         not on the caller's synchronization context.</item>
/// </list>
/// <para><b>Cancellation contract</b></para>
/// <list type="bullet">
///   <item>When <paramref name="cancellationToken"/> is signaled, the scan stops accepting new
///         entries.</item>
///   <item>When <see cref="IngestPublishOptions.FlushOnCancellation"/> is <see langword="true"/>
///         (default), buffered entries are published before the task completes.</item>
///   <item>When <see langword="false"/>, buffered-but-unpublished entries are discarded.</item>
///   <item>Entries already published remain in the index.</item>
/// </list>
/// <para><b>Completion contract</b></para>
/// <list type="bullet">
///   <item>On successful scan completion, any remaining buffer is always flushed (final publish).</item>
///   <item>Scan exceptions are propagated after attempting a final buffer flush.</item>
/// </list>
/// </remarks>
public sealed class ProgressiveIndexIngestion(IIndexUpdater updater)
{
    private readonly IIndexUpdater _updater = updater;
    private int _isRunning;

    /// <summary>
    /// Raised after each successful index publish. Optional; use for UI or diagnostics.
    /// </summary>
    public event Action<IngestionPublishEvent>? Published;

    /// <summary>
    /// Streams entries from a scan into the index with progressive publishing.
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        IAsyncEnumerable<KeyValuePair<int, string>> entries,
        IngestPublishOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        Action<IngestionPublishEvent>? onPublished = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new IngestPublishOptions();
        options.Validate();
        return IngestCoreAsync(
            entries,
            static pair => new IndexedEntry(pair.Value, pair.Value),
            options,
            progress,
            onPublished,
            cancellationToken);
    }

    /// <summary>
    /// Streams <see cref="IndexedEntry"/> values from a scan into the index.
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries,
        IngestPublishOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        Action<IngestionPublishEvent>? onPublished = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new IngestPublishOptions();
        options.Validate();
        return IngestCoreAsync(
            entries,
            static pair => pair.Value,
            options,
            progress,
            onPublished,
            cancellationToken);
    }

    private async Task<IngestionResult> IngestCoreAsync<TEntry>(
        IAsyncEnumerable<KeyValuePair<int, TEntry>> entries,
        Func<KeyValuePair<int, TEntry>, IndexedEntry> toIndexedEntry,
        IngestPublishOptions options,
        IProgress<IngestionProgress>? progress,
        Action<IngestionPublishEvent>? onPublished,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new InvalidOperationException("An ingestion is already in progress on this instance.");

        try
        {
            var channel = Channel.CreateBounded<BufferedEntry>(new BoundedChannelOptions(options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

            var state = new IngestionState();

            Task producer = ProduceAsync(entries, toIndexedEntry, channel.Writer, state, cancellationToken);
            Task consumer = ConsumeAsync(channel.Reader, options, state, progress, onPublished, cancellationToken);

            bool completed = false;
            bool cancelled = false;

            try
            {
                await Task.WhenAll(producer, consumer).ConfigureAwait(false);
                completed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                await AwaitConsumerAfterCancelAsync(consumer).ConfigureAwait(false);
            }

            return state.ToResult(completed, cancelled);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private static async Task ProduceAsync<TEntry>(
        IAsyncEnumerable<KeyValuePair<int, TEntry>> entries,
        Func<KeyValuePair<int, TEntry>, IndexedEntry> toIndexedEntry,
        ChannelWriter<BufferedEntry> writer,
        IngestionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pair in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var indexed = toIndexedEntry(pair);
                var buffered = new BufferedEntry(pair.Key, indexed, Stopwatch.GetTimestamp());
                await writer.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
                state.IncrementAccepted();
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

    private async Task ConsumeAsync(
        ChannelReader<BufferedEntry> reader,
        IngestPublishOptions options,
        IngestionState state,
        IProgress<IngestionProgress>? progress,
        Action<IngestionPublishEvent>? onPublished,
        CancellationToken cancellationToken)
    {
        var buffer = new List<KeyValuePair<int, IndexedEntry>>();
        var bufferedAt = new List<long>();

        long lastPublishTimestamp = Stopwatch.GetTimestamp();
        TimeSpan lastRebuildDuration = TimeSpan.Zero;
        bool flushOnCancel = options.FlushOnCancellation;

        try
        {
            while (true)
            {
                if (buffer.Count > 0 && ShouldPublishForTimer(options, lastRebuildDuration, lastPublishTimestamp, bufferedAt, buffer.Count))
                {
                    PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);
                }

                if (reader.Completion.IsCompleted)
                {
                    while (reader.TryRead(out BufferedEntry entry))
                        AppendEntry(buffer, bufferedAt, entry);

                    if (buffer.Count > 0)
                        PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);

                    break;
                }

                if (buffer.Count > 0 && ShouldPublishForBatch(buffer.Count, options))
                {
                    PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);
                    continue;
                }

                TimeSpan delay = ComputeDelay(options, lastRebuildDuration, lastPublishTimestamp, buffer.Count);
                if (delay > TimeSpan.Zero)
                {
                    Task<bool> readTask = WaitForEntryAsync(reader, cancellationToken);
                    Task winner = await Task.WhenAny(readTask, Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
                    if (winner != readTask
                        && buffer.Count > 0
                        && ShouldPublishForTimer(options, lastRebuildDuration, lastPublishTimestamp, bufferedAt, buffer.Count))
                    {
                        PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);
                        continue;
                    }

                    if (!await readTask.ConfigureAwait(false))
                        continue;
                }
                else if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                while (reader.TryRead(out BufferedEntry entry))
                {
                    AppendEntry(buffer, bufferedAt, entry);

                    if (ShouldPublishForBatch(buffer.Count, options))
                    {
                        PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (flushOnCancel && buffer.Count > 0)
            {
                PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration);
            }

            throw;
        }
    }

    private static async Task<bool> WaitForEntryAsync(
        ChannelReader<BufferedEntry> reader,
        CancellationToken cancellationToken)
    {
        if (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            return true;

        return false;
    }

    private static async Task AwaitConsumerAfterCancelAsync(Task consumer)
    {
        try
        {
            await consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation was requested.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw;
        }
    }

    private void PublishBatch(
        List<KeyValuePair<int, IndexedEntry>> buffer,
        List<long> bufferedAt,
        IngestionState state,
        IngestPublishOptions options,
        IProgress<IngestionProgress>? progress,
        Action<IngestionPublishEvent>? onPublished,
        ref long lastPublishTimestamp,
        ref TimeSpan lastRebuildDuration)
    {
        if (buffer.Count == 0)
            return;

        long publishStarted = Stopwatch.GetTimestamp();
        state.ObserveStaleness(bufferedAt, publishStarted);

        _updater.AddOrUpdateEntries(buffer);

        var rebuildDuration = Stopwatch.GetElapsedTime(publishStarted);
        lastRebuildDuration = rebuildDuration;
        lastPublishTimestamp = Stopwatch.GetTimestamp();

        int publishedCount = buffer.Count;
        int publishSequence = state.PublishCount;
        state.AddPublished(publishedCount, rebuildDuration);

        var publishEvent = new IngestionPublishEvent
        {
            EntryCount = publishedCount,
            IndexedDocumentCount = _updater.EntryCount,
            RebuildDuration = rebuildDuration,
            PublishSequence = publishSequence,
        };

        Published?.Invoke(publishEvent);
        onPublished?.Invoke(publishEvent);

        progress?.Report(new IngestionProgress
        {
            EntriesAccepted = state.EntriesAccepted,
            EntriesPublished = state.EntriesPublished,
            PublishCount = state.PublishCount,
            PendingCount = 0,
        });

        buffer.Clear();
        bufferedAt.Clear();
    }

    private static void AppendEntry(
        List<KeyValuePair<int, IndexedEntry>> buffer,
        List<long> bufferedAt,
        BufferedEntry entry)
    {
        buffer.Add(new KeyValuePair<int, IndexedEntry>(entry.Id, entry.Entry));
        bufferedAt.Add(entry.BufferedAtTimestamp);
    }

    private static bool ShouldPublishForBatch(int bufferCount, IngestPublishOptions options)
    {
        return options.Policy switch
        {
            IngestPublishPolicy.PerEntry => bufferCount >= 1,
            IngestPublishPolicy.FixedBatch => bufferCount >= options.FixedBatchSize,
            IngestPublishPolicy.TimeDebounce => false,
            IngestPublishPolicy.Adaptive => bufferCount >= options.FixedBatchSize,
            _ => false,
        };
    }

    private static bool ShouldPublishForTimer(
        IngestPublishOptions options,
        TimeSpan lastRebuildDuration,
        long lastPublishTimestamp,
        List<long> bufferedAt,
        int bufferCount)
    {
        if (bufferCount == 0 || bufferedAt.Count == 0)
            return false;

        if (Stopwatch.GetElapsedTime(bufferedAt[0]) >= options.MaxStaleness)
            return true;

        if (ElapsedSince(lastPublishTimestamp) < ComputeInterval(options, lastRebuildDuration))
            return false;

        return options.Policy switch
        {
            IngestPublishPolicy.TimeDebounce => true,
            IngestPublishPolicy.Adaptive => bufferCount >= options.MinTimerPublishBatch,
            _ => false,
        };
    }

    private static TimeSpan ComputeDelay(
        IngestPublishOptions options,
        TimeSpan lastRebuildDuration,
        long lastPublishTimestamp,
        int bufferCount)
    {
        if (bufferCount == 0)
            return Timeout.InfiniteTimeSpan;

        return options.Policy switch
        {
            IngestPublishPolicy.PerEntry or IngestPublishPolicy.FixedBatch => Timeout.InfiniteTimeSpan,
            IngestPublishPolicy.TimeDebounce or IngestPublishPolicy.Adaptive =>
                Max(TimeSpan.Zero, ComputeInterval(options, lastRebuildDuration) - ElapsedSince(lastPublishTimestamp)),
            _ => Timeout.InfiniteTimeSpan,
        };
    }

    private static TimeSpan ComputeInterval(IngestPublishOptions options, TimeSpan lastRebuildDuration)
    {
        return options.Policy switch
        {
            IngestPublishPolicy.TimeDebounce => options.MinInterval,
            IngestPublishPolicy.Adaptive => TimeSpan.FromMilliseconds(Math.Max(
                options.MinInterval.TotalMilliseconds,
                lastRebuildDuration.TotalMilliseconds * options.AdaptiveMultiplier)),
            _ => TimeSpan.Zero,
        };
    }

    private static TimeSpan ElapsedSince(long timestamp)
        => Stopwatch.GetElapsedTime(timestamp);

    private static TimeSpan Max(TimeSpan a, TimeSpan b)
        => a >= b ? a : b;

    private readonly record struct BufferedEntry(int Id, IndexedEntry Entry, long BufferedAtTimestamp);

    private sealed class IngestionState
    {
        private long _worstStalenessTicks;

        public int EntriesAccepted { get; private set; }
        public int EntriesPublished { get; private set; }
        public int PublishCount { get; private set; }
        public TimeSpan TotalRebuildCpu { get; private set; }

        public void IncrementAccepted() => EntriesAccepted++;

        public void AddPublished(int count, TimeSpan rebuildDuration)
        {
            EntriesPublished += count;
            PublishCount++;
            TotalRebuildCpu += rebuildDuration;
        }

        public void ObserveStaleness(List<long> bufferedAt, long publishStarted)
        {
            foreach (long buffered in bufferedAt)
            {
                TimeSpan staleness = Stopwatch.GetElapsedTime(buffered, publishStarted);
                long stalenessTicks = staleness.Ticks;
                long currentWorst = Volatile.Read(ref _worstStalenessTicks);
                while (stalenessTicks > currentWorst)
                {
                    long original = Interlocked.CompareExchange(ref _worstStalenessTicks, stalenessTicks, currentWorst);
                    if (original == currentWorst)
                        break;
                    currentWorst = Volatile.Read(ref _worstStalenessTicks);
                }
            }
        }

        public IngestionResult ToResult(bool completed, bool cancelled)
        {
            return new IngestionResult
            {
                EntriesAccepted = EntriesAccepted,
                EntriesPublished = EntriesPublished,
                PublishCount = PublishCount,
                TotalRebuildCpu = TotalRebuildCpu,
                WorstCaseStaleness = new TimeSpan(Volatile.Read(ref _worstStalenessTicks)),
                CompletedSuccessfully = completed,
                WasCancelled = cancelled,
            };
        }
    }
}
