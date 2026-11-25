using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SearchEngine.Filters;
using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

// Several tests here measure wall-clock time: gaps between publishes, cancellation after a fixed delay, and the
// rebuild time of two batch policies compared with each other. They run one at a time and with no other test
// beside them: TUnit runs every other test in parallel, and that load was enough to reverse the rebuild time
// comparison of Ingest_GrowthAwareBatchCap_ReducesPublishCountAtScale.
[NotInParallel]
public class ProgressiveIndexIngestionTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater, ProgressiveIndexIngestion ingestion) Create()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        var ingestion = new ProgressiveIndexIngestion(updater);
        return (engine, updater, ingestion);
    }

    [Test]
    public async Task Ingest_CompletesWithExactEntrySet()
    {
        var (engine, updater, ingestion) = Create();
        const int count = 5_000;

        var result = await ingestion.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(count, seed: 11),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 500,
                MinInterval = TimeSpan.FromMilliseconds(50),
            });

        await Assert.That(result.CompletedSuccessfully).IsTrue();
        await Assert.That(result.WasCancelled).IsFalse();
        await Assert.That(result.EntriesAccepted).IsEqualTo(count);
        await Assert.That(result.EntriesPublished).IsEqualTo(count);
        await Assert.That(updater.EntryCount).IsEqualTo(count);
        await Assert.That(engine.DocumentCount).IsEqualTo(count);

        var expected = SyntheticPathFeed.CreateDictionary(count, seed: 11);
        foreach (var (id, text) in expected)
        {
            await Assert.That(updater.ContainsEntry(id)).IsTrue();
            _ = text;
        }
    }

    [Test]
    public async Task Ingest_PublishesProgressivelyDuringScan()
    {
        var (engine, _, ingestion) = Create();
        const int count = 8_000;
        var publishCounts = new List<int>();
        var sawPartialIndex = false;

        var result = await ingestion.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(count, seed: 3, delayPerEntry: TimeSpan.FromMilliseconds(1)),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.FixedBatch,
                FixedBatchSize = 400,
            },
            onPublished: evt =>
            {
                publishCounts.Add(evt.IndexedDocumentCount);
                if (evt.IndexedDocumentCount < count)
                    sawPartialIndex = true;
            });

        await Assert.That(result.CompletedSuccessfully).IsTrue();
        await Assert.That(publishCounts.Count).IsGreaterThanOrEqualTo(3).Because($"Expected multiple publishes, got {publishCounts.Count}.");
        await Assert.That(sawPartialIndex).IsTrue().Because("Expected at least one publish before the full set was indexed.");
        await Assert.That(engine.DocumentCount).IsEqualTo(count);
    }

    [Test]
    public async Task Ingest_CancellationFlushesBufferByDefault()
    {
        var (_, updater, ingestion) = Create();
        using var cts = new CancellationTokenSource();

        var feed = FeedUntilCancelledAsync(cts.Token);
        var ingestTask = ingestion.IngestAsync(
            feed,
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.TimeDebounce,
                MinInterval = TimeSpan.FromMilliseconds(200),
                FixedBatchSize = 50_000,
            },
            cancellationToken: cts.Token);

        await Task.Delay(150);
        cts.Cancel();

        var result = await ingestTask;

        await Assert.That(result.WasCancelled).IsTrue();
        await Assert.That(result.CompletedSuccessfully).IsFalse();
        await Assert.That(result.EntriesPublished).IsBetween(1, result.EntriesAccepted);
        await Assert.That(updater.EntryCount).IsEqualTo(result.EntriesPublished);
    }

    [Test]
    public async Task Ingest_CancellationWithoutFlush_DiscardsBuffer()
    {
        var (_, updater, ingestion) = Create();
        using var cts = new CancellationTokenSource();

        var feed = FeedUntilCancelledAsync(cts.Token);
        var ingestTask = ingestion.IngestAsync(
            feed,
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.TimeDebounce,
                MinInterval = TimeSpan.FromMilliseconds(500),
                FixedBatchSize = 50_000,
                FlushOnCancellation = false,
            },
            cancellationToken: cts.Token);

        await Task.Delay(120);
        cts.Cancel();

        var result = await ingestTask;

        await Assert.That(result.WasCancelled).IsTrue();
        await Assert.That(result.EntriesPublished).IsEqualTo(0);
        await Assert.That(updater.EntryCount).IsEqualTo(0);
    }

    [Test]
    public async Task Ingest_ReadWhileWriteStress_NeverThrowsOrReturnsUnknownIds()
    {
        var (engine, _, ingestion) = Create();
        const int count = 15_000;
        using var queryCts = new CancellationTokenSource();

        var finalIds = new HashSet<int>(Enumerable.Range(0, count));
        var exceptions = new ConcurrentQueue<Exception>();
        var observedIds = new ConcurrentDictionary<int, byte>();

        var queryTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!queryCts.Token.IsCancellationRequested)
            {
                try
                {
                    var hits = engine.Find("file", WordMatchMethod.Within);
                    foreach (int id in hits)
                    {
                        if (!observedIds.TryAdd(id, 0) && !finalIds.Contains(id))
                            exceptions.Enqueue(new InvalidOperationException($"Unknown id {id}"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Enqueue(ex);
                }

                try
                {
                    await Task.Delay(1, queryCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, queryCts.Token)).ToArray();

        var ingestResult = await ingestion.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(count, seed: 99),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 750,
                MinInterval = TimeSpan.FromMilliseconds(25),
                AdaptiveMultiplier = 2.0,
            });

        queryCts.Cancel();
        await Task.WhenAll(queryTasks);

        await Assert.That(ingestResult.CompletedSuccessfully).IsTrue();
        await Assert.That(exceptions).IsEmpty();
        await Assert.That(engine.DocumentCount).IsEqualTo(count);
    }

    [Test]
    public async Task Ingest_WithFacets_SupportsFilterQueriesDuringAndAfterScan()
    {
        var (engine, _, ingestion) = Create();
        const int count = 3_000;
        var filter = FacetFilter.Range("size", 50_000, long.MaxValue);
        var sawPartialHits = false;

        // Query from the publish callback: every intermediate snapshot is observed, independent of timing.
        var result = await ingestion.IngestAsync(
            FacetedPathFeed.EnumerateAsync(count, seed: 17),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.FixedBatch,
                FixedBatchSize = 300,
            },
            onPublished: _ =>
            {
                int hits = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Count;
                if (hits > 0 && hits < count - 500)
                    sawPartialHits = true;
            });

        await Assert.That(result.CompletedSuccessfully).IsTrue();
        await Assert.That(sawPartialHits).IsTrue().Because("Expected filter-only queries to return partial results during ingestion.");
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Count).IsEqualTo(count - 500);
    }

    [Test]
    public async Task Ingest_GrowthAwareBatchCap_ReducesPublishCountAtScale()
    {
        var (_, _, ingestion) = Create();
        const int count = 20_000;

        var growth = await ingestion.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(count, seed: 42),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 2_000,
                GrowthAwareBatchCap = true,
                // Only the batch cap may publish: timer publishes depend on machine load.
                MaxStaleness = TimeSpan.FromHours(1),
                MinTimerPublishBatch = int.MaxValue,
            });

        var (_, _, ingestionFixed) = Create();
        var fixedBatch = await ingestionFixed.IngestAsync(
            SyntheticPathFeed.EnumerateAsync(count, seed: 42),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 2_000,
                GrowthAwareBatchCap = false,
                // Only the batch cap may publish: timer publishes depend on machine load.
                MaxStaleness = TimeSpan.FromHours(1),
                MinTimerPublishBatch = int.MaxValue,
            });

        await Assert.That(growth.CompletedSuccessfully).IsTrue();
        await Assert.That(fixedBatch.CompletedSuccessfully).IsTrue();
        await Assert.That(growth.PublishCount).IsLessThan(fixedBatch.PublishCount)
            .Because($"Expected fewer publishes with growth-aware cap (growth={growth.PublishCount}, fixed={fixedBatch.PublishCount}).");
        await Assert.That(growth.PublishCount).IsEqualTo(5);      // 2k, 4k, 8k, 16k, final 20k
        await Assert.That(fixedBatch.PublishCount).IsEqualTo(10); // every 2k
        await Assert.That(growth.TotalRebuildCpu).IsLessThan(fixedBatch.TotalRebuildCpu);
    }

    [Test]
    public async Task Ingest_RejectsConcurrentRuns()
    {
        var (_, _, ingestion) = Create();
        using var gate = new ManualResetEventSlim(false);

        var first = ingestion.IngestAsync(WaitOnGateAsync(gate));
        await Task.Delay(50);
        await Assert.That(() =>
            (Task)ingestion.IngestAsync(SyntheticPathFeed.EnumerateAsync(10))).ThrowsExactly<InvalidOperationException>();

        gate.Set();
        await first;
    }

    private static async IAsyncEnumerable<KeyValuePair<int, string>> FeedUntilCancelledAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int id = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new KeyValuePair<int, string>(id++, $"token{id}");
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async IAsyncEnumerable<KeyValuePair<int, string>> WaitOnGateAsync(
        ManualResetEventSlim gate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Run(() => gate.Wait(cancellationToken), cancellationToken);
        yield return new KeyValuePair<int, string>(1, "blocked");
    }

    private static class FacetedPathFeed
    {
        public static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> EnumerateAsync(
            int count,
            int seed = 42,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (id, text) in SyntheticPathFeed.CreateDictionary(count, seed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new KeyValuePair<int, IndexedEntry>(
                    id,
                    new IndexedEntry(text, text, FacetValues.Create("size", id * 100L)));
                await Task.Yield();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Adaptive_WhenRebuildExceedsMaxStaleness_BacksOffUnlessStrict(bool strict)
    {
        // Every rebuild takes 120 ms, longer than MaxStaleness (30 ms): each new batch is stale
        // as soon as the previous rebuild ends.
        var updater = new HookedUpdater(
            new IndexUpdater(new IndexSnapshotProvider()),
            () => Thread.Sleep(TimeSpan.FromMilliseconds(120)));
        var ingestion = new ProgressiveIndexIngestion(updater);
        var publishEnds = new List<(long End, TimeSpan Rebuild)>();

        await ingestion.IngestAsync(
            SlowFeed(count: 600, every: 10, delay: TimeSpan.FromMilliseconds(15)),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 1_000_000,
                MaxStaleness = TimeSpan.FromMilliseconds(30),
                MinInterval = TimeSpan.Zero,
                MinTimerPublishBatch = 1,
                StrictMaxStaleness = strict,
            },
            onPublished: e => publishEnds.Add((System.Diagnostics.Stopwatch.GetTimestamp(), e.RebuildDuration)));

        // Gap between the end of one publish and the start of the next.
        var gaps = new List<TimeSpan>();
        for (int i = 1; i < publishEnds.Count - 1; i++) // the last publish is the final flush
        {
            long nextStart = publishEnds[i].End - publishEnds[i].Rebuild.Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            gaps.Add(System.Diagnostics.Stopwatch.GetElapsedTime(publishEnds[i - 1].End, nextStart));
        }

        await Assert.That(gaps).IsNotEmpty();
        if (strict)
            await Assert.That(gaps.Min()).IsLessThan(TimeSpan.FromMilliseconds(60)).Because($"strict: smallest gap {gaps.Min().TotalMilliseconds} ms");
        else
            await Assert.That(gaps.Min()).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)).Because($"backoff: smallest gap {gaps.Min().TotalMilliseconds} ms (expected >= 2 x 120 ms)");
    }

    public enum PublishFault { Updater, OnPublishedCallback, PublishedEvent }

    [Test]
    [Arguments(PublishFault.Updater)]
    [Arguments(PublishFault.OnPublishedCallback)]
    [Arguments(PublishFault.PublishedEvent)]
    public async Task Ingest_PublisherFaultsWhileScannerIsBlockedOnFullChannel_FaultsWithOriginalException(PublishFault where)
    {
        // 30,000 entries, channel capacity 10,000: by the time the publisher fails on its first batch the
        // scanner has filled the channel and is blocked writing the next entry.
        const int count = 30_000;
        const int batch = 100;
        var feed = new CountingFeed(count);
        var boom = new InvalidOperationException("publisher failed");
        Action failOnceScannerIsBlocked = () =>
        {
            feed.WaitUntilYielded(new IngestPublishOptions().ChannelCapacity + batch + 1, TestTimeout);
            Thread.Sleep(50); // let the scanner enter the blocked write
            throw boom;
        };

        var inner = new IndexUpdater(new IndexSnapshotProvider());
        IIndexUpdater updater = where == PublishFault.Updater ? new HookedUpdater(inner, failOnceScannerIsBlocked) : inner;
        var ingestion = new ProgressiveIndexIngestion(updater);
        if (where == PublishFault.PublishedEvent)
            ingestion.Published += _ => failOnceScannerIsBlocked();

        var ingest = ingestion.IngestAsync(
            feed.EnumerateAsync(),
            new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = batch },
            onPublished: where == PublishFault.OnPublishedCallback ? _ => failOnceScannerIsBlocked() : null);

        var thrown = await Assert.That(() => (Task)ingest.WaitAsync(TestTimeout)).ThrowsExactly<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(boom);
        await Assert.That(feed.Disposed).IsTrue().Because("The scanner's enumerator must be disposed.");
        await Assert.That(feed.Yielded).IsLessThan(count).Because("The scanner must have been stopped, not drained.");

    }

    [Test]
    public async Task Ingest_ScannerFaultsWhilePublisherWaits_FlushesBufferThenFaultsWithScannerException()
    {
        var updater = new IndexUpdater(new IndexSnapshotProvider());
        var ingestion = new ProgressiveIndexIngestion(updater);
        var scanFault = new IOException("scan failed");

        async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            for (int i = 0; i < 50; i++)
                yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));

            await Task.Delay(100); // the publisher is waiting for entries when the scan fails
            throw scanFault;
        }

        var ingest = ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = 1_000 });

        var thrown = await Assert.That(() => (Task)ingest.WaitAsync(TestTimeout)).ThrowsExactly<IOException>();

        await Assert.That(thrown).IsSameReferenceAs(scanFault);
        await Assert.That(updater.EntryCount).IsEqualTo(50);
    }

    [Test]
    public async Task Ingest_ScannerAndPublisherBothFault_ScannerExceptionSurfaces()
    {
        var scanFault = new IOException("scan failed");
        var publishFault = new InvalidOperationException("final flush failed");
        var updater = new HookedUpdater(new IndexUpdater(new IndexSnapshotProvider()), () => throw publishFault);
        var ingestion = new ProgressiveIndexIngestion(updater);

        async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            for (int i = 0; i < 5; i++)
                yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));

            await Task.Yield();
            throw scanFault;
        }

        var ingest = ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = 1_000 });

        var thrown = await Assert.That(() => (Task)ingest.WaitAsync(TestTimeout)).ThrowsExactly<IOException>();

        await Assert.That(thrown).IsSameReferenceAs(scanFault);
    }

    [Test]
    public async Task Ingest_CancelledWhileScannerIsBlockedOnFullChannel_PublishesEverythingItAccepted()
    {
        // Capacity 50, batch 10. The first rebuild blocks; the scanner fills the channel and then
        // blocks on entry 61. Cancelling must publish the 50 accepted entries still queued in the channel.
        var feed = new CountingFeed(1_000);
        using var releaseRebuild = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        int rebuilds = 0;
        var updater = new IndexUpdater(new IndexSnapshotProvider());
        var ingestion = new ProgressiveIndexIngestion(new HookedUpdater(updater, () =>
        {
            if (Interlocked.Increment(ref rebuilds) == 1)
                releaseRebuild.Wait(TestTimeout);
        }));

        // IngestAsync runs synchronously until its first real await, and the blocked rebuild would hold
        // this thread: start it on the pool.
        var ingest = Task.Run(() => ingestion.IngestAsync(
            feed.EnumerateAsync(),
            new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = 10, ChannelCapacity = 50 },
            cancellationToken: cts.Token));

        feed.WaitUntilYielded(10 + 50 + 1, TestTimeout);
        await Task.Delay(50); // let the scanner enter the blocked write
        cts.Cancel();
        releaseRebuild.Set();
        var result = await ingest.WaitAsync(TestTimeout);

        await Assert.That(result.WasCancelled).IsTrue();
        await Assert.That(result.EntriesAccepted).IsEqualTo(60);
        await Assert.That(result.EntriesPublished).IsEqualTo(60);
        await Assert.That(updater.EntryCount).IsEqualTo(60);
        await Assert.That(feed.Disposed).IsTrue();
    }

    /// <summary>A scanner that yields <c>count</c> entries as fast as it is allowed to and records how far it got.</summary>
    private sealed class CountingFeed(int count)
    {
        private int _yielded;
        private int _disposed;

        public int Yielded => Volatile.Read(ref _yielded);
        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        // Runs on the publisher's thread inside a rebuild or a publish callback, so it fails synchronously.
        public void WaitUntilYielded(int target, TimeSpan timeout)
        {
            var spin = new SpinWait();
            var elapsed = global::System.Diagnostics.Stopwatch.StartNew();
            while (Yielded < target)
            {
                if (elapsed.Elapsed >= timeout)
                    Fail.Test($"The scanner stalled at {Yielded} of {target} entries.");
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        public async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _yielded);
                    yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));
                }

                await Task.CompletedTask;
            }
            finally
            {
                Volatile.Write(ref _disposed, 1);
            }
        }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Adaptive_TimerWakingEarly_RearmsInsteadOfWaitingForNextEntry()
    {
        // Task.Delay truncates to whole milliseconds, so a timer may fire just before the publish
        // gate opens. The publisher must re-arm for the remaining time, not wait for the next entry.
        var clock = new ManualTimeProvider();
        var updater = new HookedUpdater(
            new IndexUpdater(new IndexSnapshotProvider()),
            () => clock.Advance(TimeSpan.FromMilliseconds(400)));
        var ingestion = new ProgressiveIndexIngestion(updater, clock);
        var publishes = new ConcurrentQueue<int>();
        var scannerAtRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            for (int i = 0; i < 25; i++)
                yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));

            scannerAtRest.SetResult();
            await finishScan.Task; // the stream stays open and silent
        }

        var ingest = ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 20,
                GrowthAwareBatchCap = false,
                MinTimerPublishBatch = 1,
            },
            onPublished: e => publishes.Enqueue(e.EntryCount));

        await scannerAtRest.Task.WaitAsync(TestTimeout);
        // 20 entries publish by batch size; the remaining 5 can only leave through the timer.
        await clock.FireTimersUntilAsync(() => publishes.Count >= 2, early: TimeSpan.FromMilliseconds(1), TestTimeout);

        await Assert.That(publishes.ToArray()).IsEquivalentTo([20, 5], CollectionOrdering.Matching);
        finishScan.SetResult();
        await Assert.That((await ingest.WaitAsync(TestTimeout)).CompletedSuccessfully).IsTrue();
    }

    [Test]
    [Arguments(false, 1200)] // gated: an entry buffered when a rebuild starts waits (1 + k) x rebuild
    [Arguments(true, 1000)]  // strict: MaxStaleness
    public async Task Adaptive_StaleEntriesPublishWithoutFurtherScannerActivity(bool strict, int expectedStalenessMs)
    {
        // Rebuild 400 ms, k = 2, MaxStaleness 1 s. Five entries arrive while the first rebuild runs.
        // The gate opens at lastPublishEnd + k x rebuild = 1200 ms, when they are 1200 ms old.
        var clock = new ManualTimeProvider();
        using var rebuildEntered = new ManualResetEventSlim();
        using var entriesBuffered = new ManualResetEventSlim();
        int rebuilds = 0;
        var updater = new HookedUpdater(new IndexUpdater(new IndexSnapshotProvider()), () =>
        {
            if (Interlocked.Increment(ref rebuilds) == 1)
            {
                rebuildEntered.Set();
                entriesBuffered.Wait(TestTimeout); // the clock stands still while the second batch arrives
            }

            clock.Advance(TimeSpan.FromMilliseconds(400));
        });
        var ingestion = new ProgressiveIndexIngestion(updater, clock);
        var publishes = new ConcurrentQueue<int>();
        var finishScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            for (int i = 0; i < 20; i++)
                yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));

            await Task.Run(() => rebuildEntered.Wait(TestTimeout));
            for (int i = 20; i < 25; i++)
                yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));

            // Runs only after the publisher accepted the last entry above.
            entriesBuffered.Set();
            await finishScan.Task;
        }

        var ingest = ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.Adaptive,
                FixedBatchSize = 20,
                GrowthAwareBatchCap = false,
                StrictMaxStaleness = strict,
            },
            onPublished: e => publishes.Enqueue(e.EntryCount));

        // Five entries are below MinTimerPublishBatch: only staleness may publish them.
        await clock.FireTimersUntilAsync(() => publishes.Count >= 2, early: TimeSpan.Zero, TestTimeout);
        finishScan.SetResult();
        var result = await ingest.WaitAsync(TestTimeout);

        await Assert.That(publishes.ToArray()).IsEquivalentTo([20, 5], CollectionOrdering.Matching);
        await Assert.That(result.WorstCaseStaleness).IsEqualTo(TimeSpan.FromMilliseconds(expectedStalenessMs));
    }

    /// <summary>
    /// A clock that moves only when a test or a simulated rebuild moves it. Timers fire when a test
    /// calls <see cref="FireTimersUntilAsync"/>, optionally just before they are due, like a real
    /// timer that wakes a fraction of a millisecond early.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _now);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>
        /// Fires the earliest armed timer until <paramref name="done"/> holds. Only the first firing is
        /// <paramref name="early"/> (before its due time): a timer re-armed for the remaining
        /// sub-millisecond must fire on time or the clock would never get there.
        /// </summary>
        public async Task FireTimersUntilAsync(Func<bool> done, TimeSpan early, TimeSpan timeout)
        {
            var elapsed = global::System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                await Assert.That(elapsed.Elapsed).IsLessThan(timeout).Because("Timed out: no timer was armed and the expected publish never happened.");
                if (TryFireEarliest(early))
                    early = TimeSpan.Zero;
                else
                    await Task.Delay(1);
            }
        }

        private bool TryFireEarliest(TimeSpan early)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.MinBy(static t => t.DueTimestamp);
                if (next is null)
                    return false;

                _timers.Remove(next);
                Volatile.Write(ref _now, Math.Max(Volatile.Read(ref _now), next.DueTimestamp - early.Ticks));
            }

            next.Fire(); // outside the lock: the publisher continues inline and may arm or dispose timers
            return true;
        }

        private void Register(ManualTimer timer)
        {
            lock (_gate)
            {
                if (!_timers.Contains(timer))
                    _timers.Add(timer);
            }
        }

        private void Unregister(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long DueTimestamp { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    owner.Unregister(this);
                    return true;
                }

                DueTimestamp = owner.GetTimestamp() + dueTime.Ticks;
                owner.Register(this);
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => owner.Unregister(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> SlowFeed(int count, int every, TimeSpan delay)
    {
        for (int i = 0; i < count; i++)
        {
            if (i % every == 0)
                await Task.Delay(delay);
            yield return new(i, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"));
        }
    }

    /// <summary>Forwards to <paramref name="inner"/> and runs <paramref name="beforeAddOrUpdate"/> before every batch publish.</summary>
    private sealed class HookedUpdater(IndexUpdater inner, Action beforeAddOrUpdate) : IIndexUpdater
    {
        public int EntryCount => inner.EntryCount;
        public bool ContainsEntry(int id) => inner.ContainsEntry(id);
        public void RebuildFrom(IDictionary<int, string> entries) => inner.RebuildFrom(entries);
        public void RebuildFrom(IDictionary<int, IndexedEntry> entries) => inner.RebuildFrom(entries);
        public Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default) => inner.RebuildFromAsync(entries, progress, ct);
        public Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default) => inner.RebuildFromAsync(entries, progress, ct);
        public void AddEntry(int id, string text) => inner.AddEntry(id, text);
        public void AddEntry(int id, IndexedEntry entry) => inner.AddEntry(id, entry);
        public bool RemoveEntry(int id) => inner.RemoveEntry(id);
        public bool RefreshEntry(int id, string text) => inner.RefreshEntry(id, text);
        public bool RefreshEntry(int id, IndexedEntry entry) => inner.RefreshEntry(id, entry);
        public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries) { beforeAddOrUpdate(); inner.AddOrUpdateEntries(entries); }
        public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries) { beforeAddOrUpdate(); inner.AddOrUpdateEntries(entries); }
        public int RemoveEntries(IEnumerable<int> ids) => inner.RemoveEntries(ids);
        public void Clear() => inner.Clear();
    }
}
