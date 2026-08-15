using SearchEngine;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

public class BigramIndexTests
{
    [Fact]
    public void Build_DeduplicatesWordOrdinalPerBigramKey()
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

        Assert.True(snapshot.BigramWordIndices.TryGetValue(bigramAn, out int[]? anList));
        Assert.True(snapshot.BigramWordIndices.TryGetValue(bigramNa, out int[]? naList));

        Assert.Equal(1, anList!.Count(i => i == wordIndex));
        Assert.Equal(1, naList!.Count(i => i == wordIndex));
    }
}
