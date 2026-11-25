using System.Threading.Channels;

namespace SearchEngine.Ingestion;

/// <summary>
/// Feeds a running scan into an <see cref="IIndexUpdater"/> in batches, so the index can be queried while the scan
/// is still going. A publish policy decides when each batch is published.
/// </summary>
/// <param name="updater">The updater that receives the batches through
/// <see cref="IIndexUpdater.AddOrUpdateEntries(IEnumerable{KeyValuePair{int, IndexedEntry}})"/>.</param>
/// <remarks>
/// <para>Concurrency</para>
/// <list type="bullet">
///   <item>Only one <c>IngestAsync</c> call may run on an instance at a time.</item>
///   <item>The scan writes into a bounded channel. When the channel is full, the scan waits until the publisher has
///         taken entries from it.</item>
///   <item>Queries through an <see cref="ISearchEngine"/> on the same <see cref="IIndexSnapshotProvider"/> are safe
///         while the ingestion runs.</item>
///   <item><see cref="Published"/>, <c>onPublished</c> and <c>progress</c> run on the publisher task, not on the
///         caller's synchronization context. Marshal to a UI thread yourself.</item>
/// </list>
/// <para>Completion and cancellation</para>
/// <list type="bullet">
///   <item>When the scan ends, the remaining buffer is published.</item>
///   <item>When the scan throws, the buffer is published first and the task then faults with the scan's
///         exception.</item>
///   <item>When the <c>cancellationToken</c> passed to <c>IngestAsync</c> is cancelled, the scan stops. With
///         <see cref="IngestPublishOptions.FlushOnCancellation"/> set (the default), every accepted entry that is
///         not yet published, including entries still in the channel, is published before the task completes;
///         otherwise those entries are discarded. Entries already published stay in the index. The task completes
///         with <see cref="IngestionResult.WasCancelled"/> set and does not throw.</item>
/// </list>
/// <para>Failures</para>
/// <list type="bullet">
///   <item>An exception from the <see cref="IIndexUpdater"/>, a <see cref="Published"/> or <c>onPublished</c>
///         handler, or <see cref="IProgress{T}.Report"/> stops the publisher. The scan is cancelled through the token
///         passed to its enumerator, the enumerator is disposed, and the task faults with that exception. Entries
///         already published stay in the index; buffered ones are not published.</item>
///   <item>If the scan and the publisher both fail (for example, the final publish after a scan error fails), the
///         scan's exception is the one that surfaces.</item>
///   <item>The task completes only after the scan has stopped, so the scan must observe the token it receives in
///         <c>GetAsyncEnumerator</c> (an iterator with an <c>[EnumeratorCancellation]</c> parameter does). An
///         enumerator that ignores it and never yields again keeps the task pending.</item>
/// </list>
/// </remarks>
public sealed class ProgressiveIndexIngestion(IIndexUpdater updater)
{
    private readonly IIndexUpdater _updater = updater;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private int _isRunning;

    /// <summary>
    /// Creates an instance whose clock and timers come from <paramref name="timeProvider"/>.
    /// Tests use this to drive staleness and pacing deterministically.
    /// </summary>
    internal ProgressiveIndexIngestion(IIndexUpdater updater, TimeProvider timeProvider)
        : this(updater)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Raised after each publish, on the publisher task. An exception from a handler stops the ingestion.
    /// </summary>
    public event Action<IngestionPublishEvent>? Published;

    /// <summary>
    /// Reads string entries from a scan and publishes them to the updater in batches. Each text is indexed as
    /// <c>new IndexedEntry(text, text)</c>.
    /// </summary>
    /// <param name="entries">The scan. It must observe the cancellation token it receives.</param>
    /// <param name="options">The publish policy and its settings; the defaults when <see langword="null"/>.</param>
    /// <param name="progress">Receives counters after each publish, or <see langword="null"/>.</param>
    /// <param name="onPublished">Called after each publish, after the <see cref="Published"/> event, or
    /// <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the scan. The task then completes with
    /// <see cref="IngestionResult.WasCancelled"/> set.</param>
    /// <returns>A task that completes when the scan has ended and the last batch is published, with the counters
    /// of this call.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A property of <paramref name="options"/> is out of range;
    /// thrown before the scan starts.</exception>
    /// <exception cref="InvalidOperationException">Another <c>IngestAsync</c> call is running on this instance.
    /// The returned task faults with it.</exception>
    /// <remarks>The entries are added to what the updater already holds, and an entry replaces the one with the
    /// same id. Use <see cref="IIndexUpdater.Clear"/> first to start from an empty index.</remarks>
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
    /// Reads <see cref="IndexedEntry"/> values from a scan and publishes them to the updater in batches.
    /// </summary>
    /// <param name="entries">The scan. It must observe the cancellation token it receives.</param>
    /// <param name="options">The publish policy and its settings; the defaults when <see langword="null"/>.</param>
    /// <param name="progress">Receives counters after each publish, or <see langword="null"/>.</param>
    /// <param name="onPublished">Called after each publish, after the <see cref="Published"/> event, or
    /// <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the scan. The task then completes with
    /// <see cref="IngestionResult.WasCancelled"/> set.</param>
    /// <returns>A task that completes when the scan has ended and the last batch is published, with the counters
    /// of this call.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A property of <paramref name="options"/> is out of range;
    /// thrown before the scan starts.</exception>
    /// <exception cref="InvalidOperationException">Another <c>IngestAsync</c> call is running on this instance.
    /// The returned task faults with it.</exception>
    /// <remarks>The entries are added to what the updater already holds, and an entry replaces the one with the
    /// same id. Use <see cref="IIndexUpdater.Clear"/> first to start from an empty index.</remarks>
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

            var state = new IngestionState(_timeProvider);

            // The publisher cancels the scanner when it fails, so a scanner blocked on a full channel
            // (or still scanning) stops instead of waiting for a reader that no longer exists.
            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task producer = ProduceAsync(entries, toIndexedEntry, channel.Writer, state, producerCts.Token);
            Task consumer = CancelProducerOnFaultAsync(
                ConsumeAsync(channel.Reader, options, state, progress, onPublished, cancellationToken),
                producerCts);

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
            }

            return state.ToResult(completed, cancelled);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private static async Task CancelProducerOnFaultAsync(Task consumer, CancellationTokenSource producerCts)
    {
        try
        {
            await consumer.ConfigureAwait(false);
        }
        catch
        {
            await producerCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProduceAsync<TEntry>(
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
                var buffered = new BufferedEntry(pair.Key, indexed, _timeProvider.GetTimestamp());
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

        long lastPublishTimestamp = _timeProvider.GetTimestamp();
        TimeSpan lastRebuildDuration = TimeSpan.Zero;
        bool flushOnCancel = options.FlushOnCancellation;
        int batchCap = options.FixedBatchSize;

        // One outstanding read survives timer wake-ups, so a timer publish never leaves an orphaned waiter behind.
        Task<bool>? pendingRead = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TimeUntilTimerPublish(options, lastRebuildDuration, lastPublishTimestamp, bufferedAt) == TimeSpan.Zero)
                {
                    PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                }

                if (reader.Completion.IsCompleted)
                {
                    while (reader.TryRead(out BufferedEntry entry))
                        AppendEntry(buffer, bufferedAt, entry);

                    if (buffer.Count > 0)
                        PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);

                    break;
                }

                if (buffer.Count > 0 && ShouldPublishForBatch(buffer.Count, options, batchCap))
                {
                    PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                    continue;
                }

                pendingRead ??= WaitForEntryAsync(reader, cancellationToken);

                // While entries are buffered under a timer-driven policy, never wait for the scanner
                // alone: a timer that wakes before its publish is due (Task.Delay truncates to whole
                // milliseconds) is re-armed for the remaining time by the next iteration of this loop.
                if (TimeUntilTimerPublish(options, lastRebuildDuration, lastPublishTimestamp, bufferedAt) is { } wait)
                {
                    using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task timer = Task.Delay(ToTimerDelay(wait), _timeProvider, timerCts.Token);
                    if (await Task.WhenAny(pendingRead, timer).ConfigureAwait(false) != pendingRead)
                        continue;

                    await timerCts.CancelAsync().ConfigureAwait(false);
                }

                bool entriesAvailable = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                if (!entriesAvailable)
                    continue;

                while (reader.TryRead(out BufferedEntry entry))
                {
                    AppendEntry(buffer, bufferedAt, entry);

                    if (ShouldPublishForBatch(buffer.Count, options, batchCap))
                    {
                        PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (flushOnCancel)
            {
                // The scanner is cancelled too. Entries it already handed over may still sit in the
                // channel; read until it has completed the channel so every accepted entry is published.
                while (await WaitForEntryAsync(reader, CancellationToken.None).ConfigureAwait(false))
                {
                    while (reader.TryRead(out BufferedEntry entry))
                        AppendEntry(buffer, bufferedAt, entry);
                }

                if (buffer.Count > 0)
                    PublishBatch(buffer, bufferedAt, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
            }

            throw;
        }
    }

    private static async Task<bool> WaitForEntryAsync(
        ChannelReader<BufferedEntry> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (reader.Completion.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            // The scanner failed and the channel is drained. The failure surfaces from the producer
            // task; the publisher must still flush its buffer first.
            return false;
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
        ref TimeSpan lastRebuildDuration,
        ref int batchCap)
    {
        if (buffer.Count == 0)
            return;

        long publishStarted = _timeProvider.GetTimestamp();
        state.ObserveStaleness(bufferedAt, publishStarted);

        _updater.AddOrUpdateEntries(buffer);

        var rebuildDuration = _timeProvider.GetElapsedTime(publishStarted);
        lastRebuildDuration = rebuildDuration;
        lastPublishTimestamp = _timeProvider.GetTimestamp();
        UpdateBatchCap(options, ref batchCap);

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

        // The scanner counts an entry after handing it to the channel, so the publisher can already have
        // published an entry that is not counted yet: the difference is clamped at zero.
        int accepted = state.EntriesAccepted;
        int published = state.EntriesPublished;
        progress?.Report(new IngestionProgress
        {
            EntriesAccepted = accepted,
            EntriesPublished = published,
            PublishCount = state.PublishCount,
            PendingCount = Math.Max(0, accepted - published),
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

    private static bool ShouldPublishForBatch(int bufferCount, IngestPublishOptions options, int batchCap)
    {
        return options.Policy switch
        {
            IngestPublishPolicy.PerEntry => bufferCount >= 1,
            IngestPublishPolicy.FixedBatch => bufferCount >= options.FixedBatchSize,
            IngestPublishPolicy.TimeDebounce => false,
            IngestPublishPolicy.Adaptive => bufferCount >= batchCap,
            _ => false,
        };
    }

    private void UpdateBatchCap(IngestPublishOptions options, ref int batchCap)
    {
        if (options.Policy != IngestPublishPolicy.Adaptive || !options.GrowthAwareBatchCap)
            return;

        int indexedCount = _updater.EntryCount;
        batchCap = Math.Max(options.FixedBatchSize, indexedCount);
    }

    /// <summary>
    /// Time until a timer-driven publish is due: <see cref="TimeSpan.Zero"/> when it is due now,
    /// <see langword="null"/> when no timer should be armed (nothing buffered, or a policy that
    /// publishes by batch size only and is not yet stale).
    /// </summary>
    private TimeSpan? TimeUntilTimerPublish(
        IngestPublishOptions options,
        TimeSpan lastRebuildDuration,
        long lastPublishTimestamp,
        List<long> bufferedAt)
    {
        if (bufferedAt.Count == 0)
            return null;

        TimeSpan sinceLastPublish = ElapsedSince(lastPublishTimestamp);
        TimeSpan untilStale = options.MaxStaleness - ElapsedSince(bufferedAt[0]);

        // Once one rebuild takes longer than MaxStaleness, every batch is already stale when the
        // previous rebuild finishes; publishing on staleness alone then rebuilds back to back every
        // ChannelCapacity entries and total work becomes quadratic. Under Adaptive, staleness may
        // not publish sooner than AdaptiveMultiplier x the last rebuild duration after that rebuild.
        if (options.Policy == IngestPublishPolicy.Adaptive && !options.StrictMaxStaleness)
            untilStale = Max(untilStale, lastRebuildDuration * options.AdaptiveMultiplier - sinceLastPublish);

        if (untilStale <= TimeSpan.Zero)
            return TimeSpan.Zero;

        TimeSpan? untilPace = options.Policy switch
        {
            IngestPublishPolicy.TimeDebounce => options.MinInterval - sinceLastPublish,
            IngestPublishPolicy.Adaptive when bufferedAt.Count >= options.MinTimerPublishBatch =>
                ComputeInterval(options, lastRebuildDuration) - sinceLastPublish,
            _ => null,
        };

        if (options.Policy is not (IngestPublishPolicy.TimeDebounce or IngestPublishPolicy.Adaptive))
            return null;

        return untilPace is { } pace ? Max(TimeSpan.Zero, Min(untilStale, pace)) : untilStale;
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

    private TimeSpan ElapsedSince(long timestamp)
        => _timeProvider.GetElapsedTime(timestamp);

    private static TimeSpan Max(TimeSpan a, TimeSpan b)
        => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b)
        => a <= b ? a : b;

    // Timers have millisecond resolution and truncate: rounding up keeps a re-armed wait from
    // degenerating into a spin over the last fraction of a millisecond. The cap keeps very large
    // options within what Task.Delay accepts; the loop simply re-arms.
    private static TimeSpan ToTimerDelay(TimeSpan value)
        => TimeSpan.FromMilliseconds(Math.Ceiling(Min(value, TimeSpan.FromDays(1)).TotalMilliseconds));

    private readonly record struct BufferedEntry(int Id, IndexedEntry Entry, long BufferedAtTimestamp);

    private sealed class IngestionState(TimeProvider timeProvider)
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
                TimeSpan staleness = timeProvider.GetElapsedTime(buffered, publishStarted);
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
