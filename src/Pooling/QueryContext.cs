using System.Buffers;
using SearchEngine.Index;

namespace SearchEngine.Pooling;

// The FastBitSets of one query. Each wraps a buffer rented from ArrayPool<ulong>.Shared, one per bitset, which Dispose
// returns.
//
// ── Cost ──
// An expression creates several intermediate bitsets. At 1M ordinals each takes 125 KB (1,000,000 bits / 8), above the
// 85,000-byte large object heap threshold, so allocating them under sustained query load would add Gen2 collections.
//
// ── Invariants ──
// A FastBitSet from this context does not own its buffer and is invalid after Dispose. recordCount is the bitset
// length, IndexSnapshot.OrdinalCount (dead ordinals included).
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
        return new(bits, recordCount);
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
        foreach (var buffer in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_leasedBuffers))
            _pool.Return(buffer, clearArray: false);
        _leasedBuffers.Clear();
    }
}
