using SearchEngine;
using SearchEngine.Index;

namespace SearchEngine.Sharp.Tests;

public class FastBitSetEnumerationTests
{
    [Fact]
    public void CopySetBitOrdinals_ReturnsSparseHits()
    {
        var bitSet = new FastBitSet(100);
        bitSet.Add(0);
        bitSet.Add(7);
        bitSet.Add(64);

        Span<int> buffer = stackalloc int[8];
        int count = bitSet.CopySetBitOrdinals(buffer);

        Assert.Equal(3, count);
        Assert.Equal([0, 7, 64], buffer[..count].ToArray());
    }
}
