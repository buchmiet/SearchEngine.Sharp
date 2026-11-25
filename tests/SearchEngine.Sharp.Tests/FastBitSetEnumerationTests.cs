using SearchEngine;
using SearchEngine.Index;

namespace SearchEngine.Sharp.Tests;

public class FastBitSetEnumerationTests
{
    [Test]
    public async Task CopySetBitOrdinals_ReturnsSparseHits()
    {
        var bitSet = new FastBitSet(100);
        bitSet.Add(0);
        bitSet.Add(7);
        bitSet.Add(64);

        Span<int> buffer = stackalloc int[8];
        int count = bitSet.CopySetBitOrdinals(buffer);
        int[] ordinals = buffer[..count].ToArray();

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(ordinals).IsEquivalentTo([0, 7, 64], CollectionOrdering.Matching);
    }
}
