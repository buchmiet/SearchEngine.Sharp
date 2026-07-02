using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SearchEngine.Filters;
using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

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

    [Fact]
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

        Assert.True(result.CompletedSuccessfully);
        Assert.False(result.WasCancelled);
        Assert.Equal(count, result.EntriesAccepted);
        Assert.Equal(count, result.EntriesPublished);
        Assert.Equal(count, updater.EntryCount);
        Assert.Equal(count, engine.DocumentCount);

        var expected = SyntheticPathFeed.CreateDictionary(count, seed: 11);
        foreach (var (id, text) in expected)
        {
            Assert.True(updater.ContainsEntry(id));
            _ = text;
        }
    }

    [Fact]
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

        Assert.True(result.CompletedSuccessfully);
        Assert.True(publishCounts.Count >= 3, $"Expected multiple publishes, got {publishCounts.Count}.");
        Assert.True(sawPartialIndex, "Expected at least one publish before the full set was indexed.");
        Assert.Equal(count, engine.DocumentCount);
    }

    [Fact]
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

        Assert.True(result.WasCancelled);
        Assert.False(result.CompletedSuccessfully);
        Assert.InRange(result.EntriesPublished, 1, result.EntriesAccepted);
        Assert.Equal(result.EntriesPublished, updater.EntryCount);
    }

    [Fact]
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

        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.EntriesPublished);
        Assert.Equal(0, updater.EntryCount);
    }

    [Fact]
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

        Assert.True(ingestResult.CompletedSuccessfully);
        Assert.Empty(exceptions);
        Assert.Equal(count, engine.DocumentCount);
    }

    [Fact]
    public async Task Ingest_WithFacets_SupportsFilterQueriesDuringAndAfterScan()
    {
        var (engine, _, ingestion) = Create();
        const int count = 3_000;
        var filter = FacetFilter.Range("size", 50_000, long.MaxValue);
        var sawPartialHits = false;

        var ingestTask = ingestion.IngestAsync(
            FacetedPathFeed.EnumerateAsync(count, seed: 17),
            new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.FixedBatch,
                FixedBatchSize = 300,
            });

        while (!ingestTask.IsCompleted)
        {
            int hits = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Count;
            if (hits > 0 && hits < count)
                sawPartialHits = true;

            await Task.Delay(5);
        }

        var result = await ingestTask;

        Assert.True(result.CompletedSuccessfully);
        Assert.True(sawPartialHits, "Expected filter-only queries to return partial results during ingestion.");
        Assert.Equal(count - 500, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Count);
    }

    [Fact]
    public async Task Ingest_RejectsConcurrentRuns()
    {
        var (_, _, ingestion) = Create();
        using var gate = new ManualResetEventSlim(false);

        var first = ingestion.IngestAsync(WaitOnGateAsync(gate));
        await Task.Delay(50);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingestion.IngestAsync(SyntheticPathFeed.EnumerateAsync(10)));

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
}
