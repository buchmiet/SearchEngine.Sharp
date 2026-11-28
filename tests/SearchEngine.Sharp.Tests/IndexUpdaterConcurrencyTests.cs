using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// Readers never lock and writers never wait for a merge: while writers replace documents and merges run on background
// threads, every snapshot a reader takes must be a consistent index, and when everything settles each id must carry
// its last version. A removal that a merge loses would show as a document counted twice.
public class IndexUpdaterConcurrencyTests
{
    private const int Documents = 600;
    private const int Writers = 2;
    private const int Readers = 3;

    [Test]
    public async Task Readers_see_consistent_snapshots_while_writers_replace_documents_and_segments_merge()
    {
        // Few segments allowed and a small fan-out: merges overlap the writers all the time.
        var policy = new MergePolicy { FanOut = 2, MaxSegments = 10 };
        var (_, updater, provider) = TestIndex.Create(null, policy);
        var initial = new Dictionary<int, string>(Documents);
        for (int id = 0; id < Documents; id++)
            initial[id] = $"doc v0 id{id}";
        updater.RebuildFrom(initial);

        var lastVersion = new int[Documents];
        using var stop = new CancellationTokenSource();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
        {
            // Each writer owns the ids with id % Writers == w, so the last version of an id is known.
            var random = new Random(w);
            for (int step = 0; step < 4000; step++)
            {
                int id = random.Next(Documents / Writers) * Writers + w;
                int version = ++lastVersion[id];
                updater.RefreshEntry(id, $"doc v{version} id{id}");
                if (step % 7 == 0)
                {
                    // Remove and add back: two changes, so a snapshot between them has one document fewer.
                    updater.RemoveEntry(id);
                    updater.AddEntry(id, $"doc v{++lastVersion[id]} id{id}");
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, Readers).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = provider.Current;
                var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));
                var ids = engine.Find("doc", WordMatchMethod.Exact);
                if (ids.Count != snapshot.DocumentCount)
                    failures.Add($"Find returned {ids.Count} ids, the snapshot has {snapshot.DocumentCount} documents");
                if (ids.Distinct().Count() != ids.Count)
                    failures.Add("an id was returned twice");
                // At most one document per writer is missing (between its RemoveEntry and AddEntry).
                if (ids.Count < Documents - Writers || ids.Count > Documents)
                    failures.Add($"{ids.Count} documents, expected {Documents - Writers}..{Documents}");
                if (engine.CountMatches("doc", WordMatchMethod.Exact) != ids.Count)
                    failures.Add("CountMatches and Find disagree");
            }
        })).ToArray();

        await Task.WhenAll(writers);
        stop.Cancel();
        await Task.WhenAll(readers);
        updater.WaitForMerges();

        await Assert.That(failures).IsEmpty();
        await Assert.That(updater.MergeFault).IsNull();
        var final = new SearchEngineSharp(provider);
        await Assert.That(final.DocumentCount).IsEqualTo(Documents);
        await Assert.That(updater.EntryCount).IsEqualTo(Documents);
        for (int id = 0; id < Documents; id += 37)
            await Assert.That(final.Find($"id{id}", WordMatchMethod.Exact)).IsEquivalentTo(new[] { id }, CollectionOrdering.Matching);
        for (int id = 0; id < Documents; id++)
        {
            var hits = final.Find($"v{lastVersion[id]}", WordMatchMethod.Exact);
            await Assert.That(hits).Contains(id).Because($"id {id} should carry its last version v{lastVersion[id]}");
        }
    }
}
