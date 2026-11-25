using SearchEngine;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

public class BigramIndexTests
{
    [Test]
    public async Task Build_DeduplicatesWordOrdinalPerBigramKey()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("banana", "banana"),
        });

        IndexSnapshot snapshot = provider.Current;
        int wordIndex = 0;

        int bigramAn = ('a' << 16) | 'n';
        int bigramNa = ('n' << 16) | 'a';

        await Assert.That(snapshot.BigramWordIndices.TryGetValue(bigramAn, out int[]? anList)).IsTrue();
        await Assert.That(snapshot.BigramWordIndices.TryGetValue(bigramNa, out int[]? naList)).IsTrue();

        await Assert.That(anList!.Count(i => i == wordIndex)).IsEqualTo(1);
        await Assert.That(naList!.Count(i => i == wordIndex)).IsEqualTo(1);
    }

    [Test]
    public async Task LazyBigramIndex_WithinMatchesBruteForceSubstringScan_AndIsSharedAcrossConcurrentFirstCallers()
    {
        var rng = new Random(5);
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 2_000; id++)
        {
            var chars = new char[rng.Next(1, 12)];
            for (int c = 0; c < chars.Length; c++) chars[c] = "abcdeéx1"[rng.Next(8)];
            entries[id] = new string(chars) + " " + new string(chars, 0, chars.Length / 2 + 1);
        }

        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(entries);
        var engine = new SearchEngineSharp(provider);
        string[] queries = ["ab", "ba", "e", "éx", "x1", "abc", "dd", "1"];

        // First Within queries race to build the lazy index; all must agree with a brute-force scan.
        var results = new List<int>[queries.Length];
        Parallel.For(0, queries.Length, q => results[q] = engine.Find(queries[q], WordMatchMethod.Within));

        for (int q = 0; q < queries.Length; q++)
        {
            var expected = entries
                .Where(e => e.Value.Split(' ').Any(token => token.Contains(queries[q], StringComparison.Ordinal)))
                .Select(e => e.Key)
                .Order()
                .ToList();
            await Assert.That(results[q].Order().ToList()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }

        await Assert.That(provider.Current.BigramWordIndices).IsSameReferenceAs(provider.Current.BigramWordIndices);
    }
}
