using SearchEngine.Index;

namespace SearchEngine.Sharp.Tests;

/// <summary>
/// Compares every FastBitSet operation with a bool[] reference over lengths around the word and vector boundaries
/// and over several densities. Each case runs twice: on a set that owns its words, and on a set that wraps a longer
/// pooled buffer whose extra words must stay untouched (the shape QueryContext uses).
/// </summary>
public class FastBitSetDifferentialTests
{
    private const ulong Sentinel = 0xA5A5_5A5A_DEAD_BEEF;
    private const int ExtraPooledWords = 5;

    private static readonly int[] Lengths =
    [
        0, 1, 63, 64, 65, 127, 128, 129, 130, 255, 256, 257, 258,
        511, 512, 513, 514, 4095, 4096, 4097, 4098, 1_000_000,
    ];

    private static readonly (double Left, double Right)[] Densities =
    [
        (0.0, 0.0), (0.001, 0.5), (0.5, 0.5), (0.9, 0.1), (1.0, 0.3), (0.3, 1.0),
    ];

    public static IEnumerable<(int Length, double LeftDensity, double RightDensity, bool Pooled)> Cases()
    {
        foreach (int length in Lengths)
        {
            foreach (var (left, right) in Densities)
            {
                yield return (length, left, right, false);
                yield return (length, left, right, true);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task AddAll_Get_Count_And_Ordinals_MatchTheReference(int length, double leftDensity, double rightDensity, bool pooled)
    {
        var expected = RandomBits(length, leftDensity, Seed(length, leftDensity, 1));
        var (bitSet, buffer) = Build(expected, pooled);

        await Assert.That(Describe(bitSet, expected, buffer)).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task IntersectWith_MatchesTheReference(int length, double leftDensity, double rightDensity, bool pooled)
        => await CheckBinary(length, leftDensity, rightDensity, pooled, static (a, b) => a & b, static (s, o) => s.IntersectWith(o));

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task UnionWith_MatchesTheReference(int length, double leftDensity, double rightDensity, bool pooled)
        => await CheckBinary(length, leftDensity, rightDensity, pooled, static (a, b) => a | b, static (s, o) => s.UnionWith(o));

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task ExceptWith_MatchesTheReference(int length, double leftDensity, double rightDensity, bool pooled)
        => await CheckBinary(length, leftDensity, rightDensity, pooled, static (a, b) => a & !b, static (s, o) => s.ExceptWith(o));

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task FillAllTrue_SetsExactlyTheFirstLengthBits(int length, double leftDensity, double rightDensity, bool pooled)
    {
        var (bitSet, buffer) = Build(RandomBits(length, leftDensity, Seed(length, leftDensity, 2)), pooled);
        bitSet.FillAllTrue();

        await Assert.That(Describe(bitSet, Filled(length, true), buffer)).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Clear_RemovesEveryBit(int length, double leftDensity, double rightDensity, bool pooled)
    {
        var (bitSet, buffer) = Build(RandomBits(length, leftDensity, Seed(length, leftDensity, 3)), pooled);
        bitSet.Clear();

        await Assert.That(Describe(bitSet, Filled(length, false), buffer)).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task CopyFrom_ReplacesTheContent(int length, double leftDensity, double rightDensity, bool pooled)
    {
        var (target, buffer) = Build(RandomBits(length, leftDensity, Seed(length, leftDensity, 4)), pooled);
        var source = RandomBits(length, rightDensity, Seed(length, rightDensity, 5));
        var (sourceSet, _) = Build(source, pooled: false);
        target.CopyFrom(sourceSet);

        await Assert.That(Describe(target, source, buffer)).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(4097)]
    public async Task CreateAllTrue_SetsExactlyTheFirstLengthBits(int length)
        => await Assert.That(Describe(FastBitSet.CreateAllTrue(length), Filled(length, true), buffer: null)).IsNull();

    [Test]
    [Arguments(1)]
    [Arguments(65)]
    [Arguments(4097)]
    public async Task CopySetBitOrdinals_RefusesADestinationOneShort(int length)
    {
        var bitSet = FastBitSet.CreateAllTrue(length);

        await Assert.That(() => bitSet.CopySetBitOrdinals(new int[length - 1])).ThrowsExactly<InvalidOperationException>();
    }

    private static async Task CheckBinary(
        int length,
        double leftDensity,
        double rightDensity,
        bool pooled,
        Func<bool, bool, bool> reference,
        Action<FastBitSet, FastBitSet> operation)
    {
        var left = RandomBits(length, leftDensity, Seed(length, leftDensity, 6));
        var right = RandomBits(length, rightDensity, Seed(length, rightDensity, 7));
        var (leftSet, leftBuffer) = Build(left, pooled);
        var (rightSet, rightBuffer) = Build(right, pooled);

        operation(leftSet, rightSet);

        var expected = new bool[length];
        for (int i = 0; i < length; i++)
            expected[i] = reference(left[i], right[i]);

        await Assert.That(Describe(leftSet, expected, leftBuffer)).IsNull();
        await Assert.That(Describe(rightSet, right, rightBuffer)).IsNull().Because("the operand must not change");
    }

    // Returns null when the set holds exactly the expected bits, otherwise the first difference found.
    private static string? Describe(FastBitSet bitSet, bool[] expected, ulong[]? buffer)
    {
        int length = expected.Length;
        if (bitSet.Length != length)
            return $"Length {bitSet.Length}, expected {length}";

        int expectedCount = 0;
        for (int i = 0; i < length; i++)
        {
            if (bitSet.Get(i) != expected[i])
                return $"Get({i}) is {!expected[i]}";
            if (expected[i])
                expectedCount++;
        }

        int count = bitSet.GetTrueCount();
        if (count != expectedCount)
            return $"GetTrueCount() is {count}, expected {expectedCount}";

        var ordinals = new int[expectedCount];
        int written = bitSet.CopySetBitOrdinals(ordinals);
        if (written != expectedCount)
            return $"CopySetBitOrdinals wrote {written}, expected {expectedCount}";

        int next = 0;
        for (int i = 0; i < length; i++)
        {
            if (expected[i] && ordinals[next++] != i)
                return $"ordinal {next - 1} is {ordinals[next - 1]}, expected {i}";
        }

        if (buffer is not null)
        {
            for (int w = buffer.Length - ExtraPooledWords; w < buffer.Length; w++)
            {
                if (buffer[w] != Sentinel)
                    return $"pooled word {w} past the set was changed";
            }
        }

        return null;
    }

    private static (FastBitSet Set, ulong[]? Buffer) Build(bool[] bits, bool pooled)
    {
        int length = bits.Length;
        ulong[]? buffer = null;
        FastBitSet bitSet;
        if (pooled)
        {
            int wordCount = (length + 63) >> 6;
            buffer = new ulong[wordCount + ExtraPooledWords];
            Array.Fill(buffer, Sentinel, wordCount, ExtraPooledWords);
            bitSet = new FastBitSet(buffer, length);
        }
        else
        {
            bitSet = new FastBitSet(length);
        }

        var indices = new List<int>();
        for (int i = 0; i < length; i++)
        {
            if (bits[i])
                indices.Add(i);
        }

        bitSet.AddAll(indices.ToArray());
        return (bitSet, buffer);
    }

    private static bool[] RandomBits(int length, double density, int seed)
    {
        var random = new Random(seed);
        var bits = new bool[length];
        for (int i = 0; i < length; i++)
            bits[i] = density >= 1.0 || random.NextDouble() < density;
        return bits;
    }

    private static bool[] Filled(int length, bool value)
    {
        var bits = new bool[length];
        Array.Fill(bits, value);
        return bits;
    }

    // Deterministic, unlike HashCode.Combine, so a failing case repeats.
    private static int Seed(int length, double density, int salt) => length * 7919 + (int)(density * 1000) * 31 + salt;
}
