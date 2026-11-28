using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SearchEngine.Index;

// A fixed-length set of snapshot ordinals, 64 per ulong.
//
// ── Data layout ──
// Ordinal i is bit (i & 63) of _bits[i >> 6]. Only the first _wordCount words belong to the set: a wrapped pooled
// buffer can be longer.
//
// ── Cost ──
// UnionWith, IntersectWith and ExceptWith (OR, AND, AND NOT) share one loop over Vector<ulong> (256 bits with AVX2,
// 128 bits with ARM64 AdvSimd) followed by a scalar tail. The population count is a scalar loop: BitOperations.PopCount
// compiles to POPCNT on x64 and to CNT with ADDV on ARM64.
internal sealed class FastBitSet
{
    private readonly ulong[] _bits;
    private readonly int _length;
    private readonly int _wordCount;

    public int Length => _length;

    public FastBitSet(int length)
    {
        _length = length;
        _wordCount = (length + 63) >> 6; // ceil(length / 64)
        _bits = new ulong[_wordCount];
    }

    // Wraps a pooled buffer, which can be longer than the set needs; the set does not own it (QueryContext).
    internal FastBitSet(ulong[] bits, int length)
    {
        _length = length;
        _wordCount = (length + 63) >> 6;
        _bits = bits;
    }

    private Span<ulong> Words => _bits.AsSpan(0, _wordCount);

    public static FastBitSet CreateAllTrue(int length)
    {
        var bitSet = new FastBitSet(length);
        bitSet.FillAllTrue();
        return bitSet;
    }

    public void FillAllTrue()
    {
        Words.Fill(ulong.MaxValue);
        // Mask off bits beyond _length in the last word so GetTrueCount stays correct.
        int remainder = _length & 63;
        if (remainder > 0)
            _bits[_wordCount - 1] = (1UL << remainder) - 1;
    }

    public void Clear() => Words.Clear();

    public void CopyFrom(FastBitSet other) => other.Words.CopyTo(Words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int index) => _bits[index >> 6] |= 1UL << (index & 63);

    public void AddAll(ReadOnlySpan<int> indices)
    {
        foreach (int index in indices)
            Add(index);
    }

    // Adds offset + index for every index: the postings of a segment hold local ordinals (IndexSegment).
    public void AddAll(ReadOnlySpan<int> indices, int offset)
    {
        foreach (int index in indices)
            Add(offset + index);
    }

    // Clears every bit that is set in mask, where mask word k covers the ordinals of word wordOffset + k of this set
    // (IndexSnapshot, Data layout: a segment starts on a word boundary).
    internal void ExceptWithWords(int wordOffset, ReadOnlySpan<ulong> mask)
        => Combine<AndNotOperation>(Words.Slice(wordOffset, mask.Length), mask);

    // Clears the bits of word wordIndex that are not in keepMask.
    internal void KeepBitsOfWord(int wordIndex, ulong keepMask) => _bits[wordIndex] &= keepMask;

    public void RemoveAll(ReadOnlySpan<int> indices)
    {
        foreach (int index in indices)
            _bits[index >> 6] &= ~(1UL << (index & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

    public void ExceptWith(FastBitSet other) => Combine<AndNotOperation>(Words, other.Words);

    public void IntersectWith(FastBitSet other) => Combine<AndOperation>(Words, other.Words);

    public void UnionWith(FastBitSet other) => Combine<OrOperation>(Words, other.Words);

    // Four words per iteration: on x64 the loop overhead otherwise limits the rate below one POPCNT per cycle
    // (FastBitSetBenchmark.GetTrueCount, 10k to 1M bits: 10-20% slower without unrolling).
    public int GetTrueCount()
    {
        ReadOnlySpan<ulong> words = Words;
        int count = 0;
        int i = 0;
        for (; i <= words.Length - 4; i += 4)
        {
            count += BitOperations.PopCount(words[i]) + BitOperations.PopCount(words[i + 1])
                   + BitOperations.PopCount(words[i + 2]) + BitOperations.PopCount(words[i + 3]);
        }

        for (; i < words.Length; i++)
            count += BitOperations.PopCount(words[i]);

        return count;
    }

    // Writes the ordinals of the set bits to destination in ascending order and returns their count; throws when
    // destination is shorter than the set-bit count. A trailing-zero count per word makes the cost O(N/64 + K) for a
    // set of length N with K set bits.
    internal int CopySetBitOrdinals(Span<int> destination)
    {
        ReadOnlySpan<ulong> words = Words;
        int written = 0;
        int limit = _length;

        for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            ulong bits = words[wordIndex];
            while (bits != 0)
            {
                int bit = BitOperations.TrailingZeroCount(bits);
                int ordinal = (wordIndex << 6) + bit;
                if (ordinal >= limit)
                    break;

                if ((uint)written >= (uint)destination.Length)
                    throw new InvalidOperationException("Destination buffer too small for set bits.");

                destination[written++] = ordinal;
                bits &= bits - 1;
            }
        }

        return written;
    }

    // Writes the ordinals of the first destination.Length set bits to destination in ascending order and returns how
    // many it wrote (fewer when the set has fewer bits). It stops at the first word it does not need: the cost is
    // O(P/64 + K) for K written ordinals found within the first P ordinals.
    internal int CopyFirstSetBitOrdinals(Span<int> destination)
    {
        ReadOnlySpan<ulong> words = Words;
        int written = 0;
        int limit = _length;

        for (int wordIndex = 0; wordIndex < words.Length && written < destination.Length; wordIndex++)
        {
            ulong bits = words[wordIndex];
            while (bits != 0 && written < destination.Length)
            {
                int ordinal = (wordIndex << 6) + BitOperations.TrailingZeroCount(bits);
                if (ordinal >= limit)
                    return written;

                destination[written++] = ordinal;
                bits &= bits - 1;
            }
        }

        return written;
    }

    // The ordinals of the set bits in ascending order, read lazily: a caller that stops early reads only the words
    // before the last ordinal it took.
    internal SetBitEnumerable EnumerateSetBits() => new(this);

    internal readonly struct SetBitEnumerable(FastBitSet set)
    {
        public SetBitEnumerator GetEnumerator() => new(set._bits, set._wordCount, set._length);
    }

    internal struct SetBitEnumerator
    {
        private readonly ulong[] _bits;
        private readonly int _wordCount;
        private readonly int _length;
        private int _wordIndex;
        private ulong _current;

        internal SetBitEnumerator(ulong[] bits, int wordCount, int length)
        {
            _bits = bits;
            _wordCount = wordCount;
            _length = length;
            _wordIndex = -1;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            while (_current == 0)
            {
                if (++_wordIndex >= _wordCount)
                    return false;

                _current = _bits[_wordIndex];
            }

            int ordinal = (_wordIndex << 6) + BitOperations.TrailingZeroCount(_current);
            if (ordinal >= _length)
                return false;

            _current &= _current - 1;
            Current = ordinal;
            return true;
        }
    }

    // Applies TOperation word by word: whole Vector<ulong> blocks first, then the remaining words one at a time.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Combine<TOperation>(Span<ulong> left, ReadOnlySpan<ulong> right)
        where TOperation : struct, IBitwiseOperation
    {
        right = right[..left.Length];
        int done = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Span<Vector<ulong>> leftBlocks = MemoryMarshal.Cast<ulong, Vector<ulong>>(left);
            ReadOnlySpan<Vector<ulong>> rightBlocks = MemoryMarshal.Cast<ulong, Vector<ulong>>(right)[..leftBlocks.Length];
            for (int i = 0; i < leftBlocks.Length; i++)
                leftBlocks[i] = TOperation.Apply(leftBlocks[i], rightBlocks[i]);
            done = leftBlocks.Length * Vector<ulong>.Count;
        }

        for (int i = done; i < left.Length; i++)
            left[i] = TOperation.Apply(left[i], right[i]);
    }

    private interface IBitwiseOperation
    {
        static abstract Vector<ulong> Apply(Vector<ulong> left, Vector<ulong> right);

        static abstract ulong Apply(ulong left, ulong right);
    }

    private readonly struct AndOperation : IBitwiseOperation
    {
        public static Vector<ulong> Apply(Vector<ulong> left, Vector<ulong> right) => left & right;

        public static ulong Apply(ulong left, ulong right) => left & right;
    }

    private readonly struct OrOperation : IBitwiseOperation
    {
        public static Vector<ulong> Apply(Vector<ulong> left, Vector<ulong> right) => left | right;

        public static ulong Apply(ulong left, ulong right) => left | right;
    }

    private readonly struct AndNotOperation : IBitwiseOperation
    {
        // Vector.AndNot(left, right) is left & ~right: one VPANDN on x64, one BIC on ARM64.
        public static Vector<ulong> Apply(Vector<ulong> left, Vector<ulong> right) => Vector.AndNot(left, right);

        public static ulong Apply(ulong left, ulong right) => left & ~right;
    }
}
