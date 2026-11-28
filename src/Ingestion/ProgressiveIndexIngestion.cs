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
///         scan hands over before it stops follow in one more publish. Entries already published stay in the index.
///         The task completes with <see cref="IngestionResult.WasCancelled"/> set and does not throw.</item>
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

    // Clock and timers come from timeProvider, so staleness and pacing can run on a fake clock (read by tests).
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
        options ??= new();
        return IngestCoreAsync(
            new ScanProducer<string>(
                entries,
                static pair => IndexedEntry.FromText(pair.Key, pair.Value, nameof(entries)),
                _timeProvider),
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
        options ??= new();
        return IngestCoreAsync(
            new ScanProducer<IndexedEntry>(
                entries,
                static pair => IndexedEntry.Require(pair.Key, pair.Value, nameof(entries)),
                _timeProvider),
            options,
            progress,
            onPublished,
            cancellationToken);
    }

    private async Task<IngestionResult> IngestCoreAsync<TEntry>(
        ScanProducer<TEntry> scan,
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
            var counters = new IngestionCounters(_timeProvider);
            var schedule = new PublishSchedule(options.Policy, _timeProvider);
            var publisher = new BatchPublisher(
                _updater, _timeProvider, schedule, counters, new(RaisePublished, onPublished, progress));
            var loop = new PublishLoop(channel.Reader, schedule, publisher, _timeProvider);

            // The publisher cancels the scanner when it fails, so a scanner blocked on a full channel
            // (or still scanning) stops instead of waiting for a reader that no longer exists.
            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task producer = scan.RunAsync(channel.Writer, counters, producerCts.Token);
            Task consumer = CancelProducerOnFaultAsync(
                loop.RunAsync(options.FlushOnCancellation, cancellationToken),
                producerCts);

            return await AwaitBothAsync(producer, consumer, counters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private static async Task<IngestionResult> AwaitBothAsync(
        Task producer,
        Task consumer,
        IngestionCounters counters,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            return counters.ToResult(completed: true, cancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return counters.ToResult(completed: false, cancelled: true);
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

    // Read at every publish, so a handler added during the ingestion hears about the later publishes.
    private void RaisePublished(IngestionPublishEvent publishEvent) => Published?.Invoke(publishEvent);
}
