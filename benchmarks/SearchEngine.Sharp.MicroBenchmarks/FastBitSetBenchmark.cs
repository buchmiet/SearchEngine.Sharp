using BenchmarkDotNet.Attributes;
using SearchEngine.Index;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The FastBitSet operations of the query pipeline by set length: AND, OR and AND NOT of two sets (boolean operators
/// and the facet full scan), the population count (hit count before materialization) and the enumeration of the set
/// bits (materialization and the facet on-hits path).
/// </summary>
/// <remarks>
/// The binary operations work in place; their cost does not depend on the bit values, so repeating them on a set
/// that has converged measures the same work. The enumeration runs at 10% density.
/// </remarks>
public class FastBitSetBenchmark
{
    private FastBitSet _left = null!;
    private FastBitSet _right = null!;
    private FastBitSet _sparse = null!;
    private int[] _ordinals = null!;

    [Params(1_000, 10_000, 100_000, 1_000_000)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(2026);
        _left = Random(Length, 0.5, random);
        _right = Random(Length, 0.5, random);
        _sparse = Random(Length, 0.1, random);
        _ordinals = new int[Length];
    }

    [Benchmark(Description = "IntersectWith (AND)")]
    public void IntersectWith() => _left.IntersectWith(_right);

    [Benchmark(Description = "UnionWith (OR)")]
    public void UnionWith() => _left.UnionWith(_right);

    [Benchmark(Description = "ExceptWith (AND NOT)")]
    public void ExceptWith() => _left.ExceptWith(_right);

    [Benchmark(Description = "GetTrueCount (popcount)")]
    public int GetTrueCount() => _right.GetTrueCount();

    [Benchmark(Description = "CopySetBitOrdinals, 10% set")]
    public int CopySetBitOrdinals() => _sparse.CopySetBitOrdinals(_ordinals);

    private static FastBitSet Random(int length, double density, Random random)
    {
        var bitSet = new FastBitSet(length);
        for (int i = 0; i < length; i++)
        {
            if (random.NextDouble() < density)
                bitSet.Add(i);
        }

        return bitSet;
    }
}
