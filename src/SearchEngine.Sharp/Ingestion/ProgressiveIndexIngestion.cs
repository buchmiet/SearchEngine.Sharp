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
///         otherwise those entries are discarded. The entries that have arrived are published at once; entries the
///         scan hands over before it stops follow in one more publish. Entries already published stay in the index. The task completes
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
/// <exception cref="ArgumentNullException"><paramref name="updater"/> is <see langword="null"/>.</exception>
public sealed class ProgressiveIndexIngestion(IIndexUpdater updater)
{
    private readonly IIndexUpdater _updater = updater ?? throw new ArgumentNullException(nameof(updater));
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
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A property of <paramref name="options"/> is out of range;
    /// thrown before the scan starts.</exception>
    /// <exception cref="ArgumentException">The scan yields a <see langword="null"/> entry. The returned task faults
    /// with it.</exception>
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
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new IngestPublishOptions();
        options.Validate();
        return IngestCoreAsync(
            entries,
            static pair => IndexedEntry.FromText(pair.Key, pair.Value, nameof(entries)),
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
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A property of <paramref name="options"/> is out of range;
    /// thrown before the scan starts.</exception>
    /// <exception cref="ArgumentException">The scan yields a <see langword="null"/> entry. The returned task faults
    /// with it.</exception>
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
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new IngestPublishOptions();
        options.Validate();
        return IngestCoreAsync(
            entries,
            static pair => IndexedEntry.Require(pair.Key, pair.Value, nameof(entries)),
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
                await WriteAcceptedAsync(writer, buffered, state, cancellationToken).ConfigureAwait(false);
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
        IngestionState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // Like ChannelWriter.WriteAsync: nothing is accepted once the ingestion is cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            state.IncrementAccepted();
            if (writer.TryWrite(entry))
                return;

            state.DecrementAccepted();
            if (!await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                throw new ChannelClosedException();
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
        var batch = new PendingBatch();

        long lastPublishTimestamp = _timeProvider.GetTimestamp();
        TimeSpan lastRebuildDuration = TimeSpan.Zero;
        bool flushOnCancel = options.FlushOnCancellation;
        int batchCap = options.FixedBatchSize;

        // One outstanding read survives timer wake-ups, so a timer publish never leaves an orphaned waiter behind.
        Task<bool>? pendingRead = null;
        using var wakeTimer = new WakeTimer(_timeProvider);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TimeUntilTimerPublish(options, lastRebuildDuration, lastPublishTimestamp, batch) == TimeSpan.Zero)
                {
                    PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                }

                if (reader.Completion.IsCompleted)
                {
                    while (reader.TryRead(out BufferedEntry entry))
                        batch.Add(entry);

                    if (batch.Count > 0)
                        PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);

                    break;
                }

                if (batch.Count > 0 && ShouldPublishForBatch(batch.Count, options, batchCap))
                {
                    PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                    continue;
                }

                pendingRead ??= WaitForEntryAsync(reader, cancellationToken);

                // While entries are buffered under a timer-driven policy, never wait for the scanner
                // alone: a timer that wakes before its publish is due (Task.Delay truncates to whole
                // milliseconds) is re-armed for the remaining time by the next iteration of this loop.
                if (TimeUntilTimerPublish(options, lastRebuildDuration, lastPublishTimestamp, batch) is { } wait)
                {
                    Task timer = wakeTimer.Arm(ToTimerDelay(wait));
                    if (await Task.WhenAny(pendingRead, timer).ConfigureAwait(false) != pendingRead)
                        continue;

                    wakeTimer.Disarm();
                }

                bool entriesAvailable = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                if (!entriesAvailable)
                    continue;

                while (reader.TryRead(out BufferedEntry entry))
                {
                    batch.Add(entry);

                    if (ShouldPublishForBatch(batch.Count, options, batchCap))
                    {
                        PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (flushOnCancel)
            {
                // Publish what has arrived at once: the scanner may take long to observe its cancellation.
                while (reader.TryRead(out BufferedEntry entry))
                    batch.Add(entry);

                PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);

                // The scanner is cancelled too. It may still hand over entries before it stops; read until it
                // has completed the channel so every accepted entry is published.
                while (await WaitForEntryAsync(reader, CancellationToken.None).ConfigureAwait(false))
                {
                    while (reader.TryRead(out BufferedEntry entry))
                        batch.Add(entry);
                }

                if (batch.Count > 0)
                    PublishBatch(batch, state, options, progress, onPublished, ref lastPublishTimestamp, ref lastRebuildDuration, ref batchCap);
            }

            throw;
        }
    }

    internal static async Task<bool> WaitForEntryAsync<T>(
        ChannelReader<T> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The scanner failed: the channel throws its exception once it is drained, sometimes before its
            // Completion task is complete. That is the end of the input. The failure surfaces from the producer
            // task; the publisher must still flush its buffer first, also when the caller cancels meanwhile.
            return false;
        }
    }

    private void PublishBatch(
        PendingBatch batch,
        IngestionState state,
        IngestPublishOptions options,
        IProgress<IngestionProgress>? progress,
        Action<IngestionPublishEvent>? onPublished,
        ref long lastPublishTimestamp,
        ref TimeSpan lastRebuildDuration,
        ref int batchCap)
    {
        if (batch.Count == 0)
            return;

        long publishStarted = _timeProvider.GetTimestamp();
        state.ObserveStaleness(batch.OldestTimestamp, publishStarted);

        _updater.AddOrUpdateEntries(batch.Entries);

        var rebuildDuration = _timeProvider.GetElapsedTime(publishStarted);
        lastRebuildDuration = rebuildDuration;
        lastPublishTimestamp = _timeProvider.GetTimestamp();
        UpdateBatchCap(options, ref batchCap);

        // The batch is in the index: count it and empty the buffer before any handler runs, so a handler that
        // throws (for example after the caller cancelled) cannot make the flush publish the same entries again.
        int publishedCount = batch.Count;
        int publishSequence = state.PublishCount;
        state.AddPublished(publishedCount, rebuildDuration);
        batch.Clear();

        var publishEvent = new IngestionPublishEvent
        {
            EntryCount = publishedCount,
            IndexedDocumentCount = _updater.EntryCount,
            RebuildDuration = rebuildDuration,
            PublishSequence = publishSequence,
        };

        Published?.Invoke(publishEvent);
        onPublished?.Invoke(publishEvent);

        int accepted = state.EntriesAccepted;
        int published = state.EntriesPublished;
        progress?.Report(new IngestionProgress
        {
            EntriesAccepted = accepted,
            EntriesPublished = published,
            PublishCount = state.PublishCount,
            PendingCount = accepted - published,
        });
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
        PendingBatch batch)
    {
        if (batch.Count == 0)
            return null;

        TimeSpan sinceLastPublish = ElapsedSince(lastPublishTimestamp);
        TimeSpan untilStale = options.MaxStaleness - ElapsedSince(batch.OldestTimestamp);

        // Once one rebuild takes longer than MaxStaleness, every batch is already stale when the
        // previous rebuild finishes; publishing on staleness alone then rebuilds back to back every
        // ChannelCapacity entries and total work becomes quadratic. Under Adaptive, staleness may
        // not publish sooner than AdaptiveMultiplier x the last rebuild duration after that rebuild.
        if (options.Policy == IngestPublishPolicy.Adaptive && !options.StrictMaxStaleness)
            untilStale = Max(untilStale, lastRebuildDuration * options.AdaptiveMultiplier - sinceLastPublish);

        if (untilStale <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return options.Policy switch
        {
            IngestPublishPolicy.TimeDebounce => EarlierOf(untilStale, options.MinInterval - sinceLastPublish),
            IngestPublishPolicy.Adaptive when batch.Count >= options.MinTimerPublishBatch => EarlierOf(
                untilStale,
                Max(options.MinInterval, lastRebuildDuration * options.AdaptiveMultiplier) - sinceLastPublish),
            IngestPublishPolicy.Adaptive => untilStale,
            _ => null, // FixedBatch and PerEntry arm no timer.
        };

        static TimeSpan EarlierOf(TimeSpan untilStale, TimeSpan untilPace)
            => Max(TimeSpan.Zero, Min(untilStale, untilPace));
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

    /// <summary>
    /// The entries taken from the channel and not yet published. The scanner stamps entries in the order it hands
    /// them over, so the first entry is the oldest and its timestamp alone decides the staleness of the batch.
    /// </summary>
    private sealed class PendingBatch
    {
        public List<KeyValuePair<int, IndexedEntry>> Entries { get; } = [];

        public long OldestTimestamp { get; private set; }

        public int Count => Entries.Count;

        public void Add(BufferedEntry entry)
        {
            if (Entries.Count == 0)
                OldestTimestamp = entry.BufferedAtTimestamp;

            Entries.Add(new KeyValuePair<int, IndexedEntry>(entry.Id, entry.Entry));
        }

        public void Clear() => Entries.Clear();
    }

    /// <summary>
    /// One timer for the whole ingestion, re-armed for each wait instead of a new delay and cancellation source per
    /// wake-up. A firing that arrives after the publisher has moved on only causes one extra pass of the loop, which
    /// re-arms the timer for the time that is really left.
    /// </summary>
    private sealed class WakeTimer : IDisposable
    {
        private readonly ITimer _timer;
        private TaskCompletionSource _fired = NewSource();

        public WakeTimer(TimeProvider timeProvider)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((WakeTimer)state!).Fire(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        /// <summary>Arms the timer and returns a task that completes when it fires.</summary>
        public Task Arm(TimeSpan dueTime)
        {
            if (_fired.Task.IsCompleted)
                Volatile.Write(ref _fired, NewSource());

            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
            return _fired.Task;
        }

        public void Disarm() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public void Dispose() => _timer.Dispose();

        private void Fire() => Volatile.Read(ref _fired).TrySetResult();

        // The publisher must not run its rebuild on the timer's callback thread.
        private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class IngestionState(TimeProvider timeProvider)
    {
        private int _entriesAccepted;

        // The scanner writes this counter and the publisher reads it.
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

        public IngestionResult ToResult(bool completed, bool cancelled)
        {
            return new IngestionResult
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
    }
}
