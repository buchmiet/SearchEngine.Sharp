using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86; // Avx, Avx2, Popcnt

namespace SearchEngine.Index;

// A fixed-capacity bitset backed by a ulong[] array where each ulong holds 64 bits.
//
// Layout: document with ordinal i maps to bit (i & 63) of word _bits[i >> 6].
//   Add(i)  →  _bits[i / 64] |= 1 << (i % 64)
//   Get(i)  →  (_bits[i / 64] >> (i % 64)) & 1
//
// Set operations map directly to boolean algebra used by the query evaluator:
//   UnionWith     → OR  (documents matching A or B)
//   IntersectWith → AND (documents matching A and B)
//   ExceptWith    → AND NOT (documents matching A but not B; used to implement NOT)
//
// Each SIMD operation processes multiple ulongs per loop iteration:
//   AVX2  (x64)   — 256-bit registers = 4 ulongs per iteration
//   AdvSimd (ARM) — 128-bit registers = 2 ulongs per iteration
//   Scalar        — 1 ulong per iteration (fallback)
//
// The width threshold guards (>= 4 or >= 2) prevent entering a SIMD loop that
// can never execute a single full-width iteration.
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

    // Used by QueryContext to wrap a pooled ulong[] buffer without extra allocation.
    internal FastBitSet(ulong[] bits, int length)
    {
        _length = length;
        _wordCount = (length + 63) >> 6;
        _bits = bits;
    }

    public static FastBitSet CreateAllTrue(int length)
    {
        var bitSet = new FastBitSet(length);
        bitSet.FillAllTrue();
        return bitSet;
    }

    public void FillAllTrue()
    {
        Array.Fill(_bits, ulong.MaxValue, 0, _wordCount);
        // Mask off bits beyond _length in the last word so GetTrueCount stays correct.
        int remainder = _length & 63;
        if (remainder > 0 && _wordCount > 0)
            _bits[_wordCount - 1] = (1UL << remainder) - 1;
    }

    public void Clear() => Array.Clear(_bits, 0, _wordCount);

    public void CopyFrom(FastBitSet other) =>
        other._bits.AsSpan(0, _wordCount).CopyTo(_bits.AsSpan(0, _wordCount));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int index) => _bits[index >> 6] |= 1UL << (index & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

    public void ExceptWith(FastBitSet other)
    {
        if (_wordCount == 0)
            return;

        if (Avx2.IsSupported && _wordCount >= 4)
        {
            ExceptVector256(other);
            return;
        }

        if (AdvSimd.IsSupported && _wordCount >= 2)
        {
            ExceptAdvSimd(other);
            return;
        }

        ExceptScalar(other, 0);
    }

    public void IntersectWith(FastBitSet other)
    {
        if (_wordCount == 0)
            return;

        if (Avx2.IsSupported && _wordCount >= 4)
        {
            IntersectVector256(other);
            return;
        }

        if (AdvSimd.IsSupported && _wordCount >= 2)
        {
            IntersectAdvSimd(other);
            return;
        }

        IntersectScalar(other, 0);
    }

    public void UnionWith(FastBitSet other)
    {
        if (_wordCount == 0)
            return;

        if (Avx2.IsSupported && _wordCount >= 4)
        {
            UnionVector256(other);
            return;
        }

        if (AdvSimd.IsSupported && _wordCount >= 2)
        {
            UnionAdvSimd(other);
            return;
        }

        UnionScalar(other, 0);
    }

    // Counts set bits across all words using the best available popcount instruction.
    // AdvSimd is checked before Popcnt because on ARM the vectorised byte-popcount +
    // horizontal-sum is faster than scalar Popcnt in a tight loop.
    public int GetTrueCount()
    {
        if (_wordCount == 0)
            return 0;

        if (AdvSimd.IsSupported && _wordCount >= 2)
            return GetTrueCountAdvSimd();

        if (Popcnt.X64.IsSupported)
            return GetTrueCountPopcnt();

        return GetTrueCountScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void ExceptVector256(FastBitSet other)
    {
        const int width = 4;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = Avx.LoadVector256(leftBase + i);
                var right = Avx.LoadVector256(rightBase + i);
                Avx.Store(leftBase + i, Avx2.AndNot(right, left));
            }

            if (i < _wordCount)
                ExceptScalar(other, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void ExceptAdvSimd(FastBitSet other)
    {
        const int width = 2;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = AdvSimd.LoadVector128(leftBase + i);
                var right = AdvSimd.LoadVector128(rightBase + i);
                // ARM BIC: left & ~right — single instruction
                AdvSimd.Store(leftBase + i, AdvSimd.BitwiseClear(left, right));
            }

            if (i < _wordCount)
                ExceptScalar(other, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void IntersectVector256(FastBitSet other)
    {
        const int width = 4;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = Avx.LoadVector256(leftBase + i);
                var right = Avx.LoadVector256(rightBase + i);
                Avx.Store(leftBase + i, Avx2.And(left, right));
            }

            if (i < _wordCount)
                IntersectScalar(other, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void IntersectAdvSimd(FastBitSet other)
    {
        const int width = 2;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = AdvSimd.LoadVector128(leftBase + i);
                var right = AdvSimd.LoadVector128(rightBase + i);
                AdvSimd.Store(leftBase + i, AdvSimd.And(left, right));
            }

            if (i < _wordCount)
                IntersectScalar(other, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void UnionVector256(FastBitSet other)
    {
        const int width = 4;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = Avx.LoadVector256(leftBase + i);
                var right = Avx.LoadVector256(rightBase + i);
                Avx.Store(leftBase + i, Avx2.Or(left, right));
            }

            if (i < _wordCount)
                UnionScalar(other, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void UnionAdvSimd(FastBitSet other)
    {
        const int width = 2;
        fixed (ulong* leftBase = _bits)
        fixed (ulong* rightBase = other._bits)
        {
            int i = 0;
            int limit = _wordCount - width;
            for (; i <= limit; i += width)
            {
                var left = AdvSimd.LoadVector128(leftBase + i);
                var right = AdvSimd.LoadVector128(rightBase + i);
                AdvSimd.Store(leftBase + i, AdvSimd.Or(left, right));
            }

            if (i < _wordCount)
                UnionScalar(other, i);
        }
    }

    private void ExceptScalar(FastBitSet other, int startIndex)
    {
        var span = _bits.AsSpan(0, _wordCount);
        var otherSpan = other._bits.AsSpan(0, _wordCount);

        for (int i = startIndex; i < _wordCount; i++)
            span[i] &= ~otherSpan[i];
    }

    private void IntersectScalar(FastBitSet other, int startIndex)
    {
        var span = _bits.AsSpan(0, _wordCount);
        var otherSpan = other._bits.AsSpan(0, _wordCount);

        for (int i = startIndex; i < _wordCount; i++)
            span[i] &= otherSpan[i];
    }

    private void UnionScalar(FastBitSet other, int startIndex)
    {
        var span = _bits.AsSpan(0, _wordCount);
        var otherSpan = other._bits.AsSpan(0, _wordCount);

        for (int i = startIndex; i < _wordCount; i++)
            span[i] |= otherSpan[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetTrueCountAdvSimd()
    {
        const int width = 2;
        int count = 0;
        var span = _bits.AsSpan(0, _wordCount);
        ref ulong bitsRef = ref Unsafe.AsRef(in span[0]);

        int i = 0;
        int limit = _wordCount - width;
        for (; i <= limit; i += width)
        {
            // Load two ulongs as a 128-bit vector, popcount each byte, then sum.
            var vector = Vector128.LoadUnsafe(ref bitsRef, (nuint)i);
            var popCountBytes = AdvSimd.PopCount(vector.AsByte());
            count += SumVectorBytes(popCountBytes);
        }

        for (; i < _wordCount; i++)
            count += BitOperations.PopCount(span[i]);

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetTrueCountPopcnt()
    {
        int count = 0;
        var span = _bits.AsSpan(0, _wordCount);

        int i = 0;
        int limit = _wordCount - 4;
        for (; i <= limit; i += 4)
        {
            count += (int)Popcnt.X64.PopCount(span[i]);
            count += (int)Popcnt.X64.PopCount(span[i + 1]);
            count += (int)Popcnt.X64.PopCount(span[i + 2]);
            count += (int)Popcnt.X64.PopCount(span[i + 3]);
        }

        for (; i < _wordCount; i++)
            count += (int)Popcnt.X64.PopCount(span[i]);

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetTrueCountScalar()
    {
        int count = 0;
        var span = _bits.AsSpan(0, _wordCount);

        int i = 0;
        int limit = _wordCount - 4;
        for (; i <= limit; i += 4)
        {
            count += BitOperations.PopCount(span[i]);
            count += BitOperations.PopCount(span[i + 1]);
            count += BitOperations.PopCount(span[i + 2]);
            count += BitOperations.PopCount(span[i + 3]);
        }

        for (; i < _wordCount; i++)
            count += BitOperations.PopCount(span[i]);

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumVectorBytes(Vector128<byte> vector)
    {
        int count = 0;
        ref byte refByte = ref Unsafe.As<Vector128<byte>, byte>(ref vector);
        for (nuint i = 0; i < (nuint)Vector128<byte>.Count; i++)
            count += Unsafe.Add(ref refByte, i);
        return count;
    }
}
