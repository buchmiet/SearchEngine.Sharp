using System.Buffers;
using SearchEngine.Index;

namespace SearchEngine.Pooling;

// Manages a set of FastBitSet instances for the duration of a single query.
//
// Why pooling?
//   Evaluating a boolean expression (e.g. "cat AND (dog OR bird) NOT fish")
//   creates several intermediate bitsets. Without pooling each bitset allocates
//   a new ulong[] — for 1M documents that is 125 KB per bitset, enough to
//   trigger Gen2 GC under sustained query load.
//
// How it works:
//   All ulong[] buffers are rented from ArrayPool<ulong>.Shared on first use
//   and returned in Dispose(). FastBitSet wraps the rented buffer — it does
//   not own it. QueryContext is created per-query (using var qc = new QueryContext(...))
//   so all leases are released when the query completes.
//
// Lifetime rule: all FastBitSets produced by this context are invalid after Dispose().
internal sealed class QueryContext(int recordCount) : IDisposable
{
    private readonly ArrayPool<ulong> _pool = ArrayPool<ulong>.Shared;
    private readonly List<ulong[]> _leasedBuffers = [];

    public FastBitSet RentEmptyBitSet()
    {
        int wordCount = (recordCount + 63) >> 6;
        var bits = _pool.Rent(wordCount);
        _leasedBuffers.Add(bits);
        Array.Clear(bits, 0, wordCount);
        return new FastBitSet(bits, recordCount);
    }

    public FastBitSet RentAllTrueBitSet()
    {
        var bitSet = RentEmptyBitSet();
        bitSet.FillAllTrue();
        return bitSet;
    }

    public FastBitSet RentCopyOf(FastBitSet source)
    {
        var copy = RentEmptyBitSet();
        copy.CopyFrom(source);
        return copy;
    }

    public void Dispose()
    {
        foreach (var buffer in _leasedBuffers)
            _pool.Return(buffer, clearArray: false);
        _leasedBuffers.Clear();
    }
}
