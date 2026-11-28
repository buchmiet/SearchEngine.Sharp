using System.Diagnostics;

namespace ThreeWay;

/// <summary>One query, expressed once per engine so each engine runs its own native form of the same question.</summary>
public sealed record QuerySpec(
    string Id,
    string Description,
    (string Expr, SearchEngine.WordMatchMethod Method, bool Ops)? Sharp,
    Func<Lucene.Net.Search.Query>? Lucene,
    Func<TantivyDotNet.Query>? Tantivy);

public interface IEngine : IDisposable
{
    string Name { get; }
    /// <summary>Builds the index in memory from the names; id is the position in the array.</summary>
    void Build(string[] names);
    /// <summary>Number of matching documents, no ids materialised.</summary>
    long Count(QuerySpec q);
    /// <summary>First ten hits including the id of each (engines that rank, rank by BM25).</summary>
    long Top10(QuerySpec q);
    /// <summary>All matching ids, materialised in managed memory.</summary>
    long All(QuerySpec q);
    /// <summary>Alternative all-ids path where the engine has one (Tantivy: stored-field reads); null otherwise.</summary>
    long? AllAlt(QuerySpec q) => null;
    bool Supports(QuerySpec q);
    /// <summary>Adds one new document and returns once a query can see it; returns the hit count of its unique token.</summary>
    long AddOneAndSearch(int id, string name, string uniqueToken);
    /// <summary>Builds the same corpus into a directory and returns (build seconds, bytes on disk, cold-open seconds).</summary>
    (double BuildSec, long Bytes, double OpenSec)? DiskBuild(string[] names, string dir);
    void ForceMerge();
}

public static class Sep
{
    // Same separators as SearchEngine.Sharp's SearchTokenization.Default, so all three engines see the same tokens.
    public const string Chars = " .,;:/\\\r\n\t|-#[]{}()~£$€";
    public const string TantivyRegex = @"[^ .,;:/\\\r\n\t|\-#\[\]{}()~£$€]+";
    public static bool IsSep(int c) => Chars.IndexOf((char)c) >= 0 && c < 0x10000;
}

public static class Timing
{
    public sealed record Result(double MedianUs, double P95Us, double MinUs, int Samples, long Checksum);

    public static Result Measure(Func<long> op, double warmupMs = 150, double budgetMs = 700, int minSamples = 15, int maxSamples = 50_000)
    {
        long check = 0;
        double freq = Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        int w = 0;
        while ((Stopwatch.GetTimestamp() - t0) / freq * 1000 < warmupMs || w < 3) { check += op(); w++; if (w > 100000) break; }
        var samples = new List<double>(1024);
        long start = Stopwatch.GetTimestamp();
        while (samples.Count < maxSamples)
        {
            long a = Stopwatch.GetTimestamp();
            check += op();
            long b = Stopwatch.GetTimestamp();
            samples.Add((b - a) / freq * 1e6);
            if (samples.Count >= minSamples && (b - start) / freq * 1000 > budgetMs) break;
        }
        samples.Sort();
        return new Result(samples[samples.Count / 2], samples[(int)(samples.Count * 0.95)], samples[0], samples.Count, check);
    }
}

public static class Proc
{
    public static long RssBytes()
    {
        foreach (var l in File.ReadLines("/proc/self/status"))
            if (l.StartsWith("VmRSS:")) return long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        return 0;
    }
    public static long DirBytes(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
