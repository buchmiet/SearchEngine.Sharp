using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

// Query throughput on synthetic data.
//
//   dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks
//   dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --parallel

int warmup     = GetInt(args, "--warmup", 3);
int iterations = GetInt(args, "--iterations", 10);
int seed       = GetInt(args, "--seed", 1337);
bool parallel  = args.Contains("--parallel");

Console.WriteLine($"Runtime  : {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Arch     : {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"SIMD     : Avx2={Avx2.IsSupported}  AdvSimd={AdvSimd.IsSupported}");
Console.WriteLine($"CPU cores: {Environment.ProcessorCount}");
Console.WriteLine();

var scenarios = new BenchScenario[]
{
    new("small",  DocumentCount:  10_000, VocabularySize:  5_000, QueryCount: 500),
    new("medium", DocumentCount: 100_000, VocabularySize: 30_000, QueryCount: 500),
    new("large",  DocumentCount: 250_000, VocabularySize: 30_000, QueryCount: 500),
};

foreach (var scenario in scenarios)
    RunQueryScenario(scenario);

if (parallel)
{
    Console.WriteLine();
    var parallelScenarios = new BenchScenario[]
    {
        new("medium",  DocumentCount: 100_000, VocabularySize: 30_000, QueryCount: 500),
        new("large",   DocumentCount: 250_000, VocabularySize: 30_000, QueryCount: 500),
        new("xlarge",  DocumentCount: 500_000, VocabularySize: 80_000, QueryCount: 500),
    };

    foreach (var scenario in parallelScenarios)
        RunParallelInfixScenario(scenario);
}

void RunQueryScenario(BenchScenario scenario)
{
    Console.WriteLine($"=== {scenario.Name} — {scenario.DocumentCount:N0} docs, {scenario.VocabularySize:N0} vocab ===");

    var data = SyntheticDataFactory.Create(scenario, seed);
    var provider = new IndexSnapshotProvider();
    var updater = new IndexUpdater(provider);
    updater.RebuildFrom(data.Documents.ToDictionary(d => d.Id, d => d.Text));
    var engine = new SearchEngineSharp(provider);

    RunQueryBench("Exact", data.ExactQueries, q => engine.Find(q, WordMatchMethod.Exact));
    RunQueryBench("Within", data.InfixQueries, q => engine.Find(q, WordMatchMethod.Within));
    RunQueryBench("Boolean", data.BooleanQueries, q => engine.Find(q, WordMatchMethod.Exact, enableOperators: true));
    Console.WriteLine();
}

void RunParallelInfixScenario(BenchScenario scenario)
{
    Console.WriteLine($"=== parallel infix — {scenario.Name} — {scenario.DocumentCount:N0} docs ===");

    var data = SyntheticDataFactory.Create(scenario, seed);
    var provider = new IndexSnapshotProvider();
    var updater = new IndexUpdater(provider);
    updater.RebuildFrom(data.Documents.ToDictionary(d => d.Id, d => d.Text));
    var snapshot = provider.Current;

    int[] threadCounts = [1, 2, 4, 8, Environment.ProcessorCount];
    threadCounts = threadCounts.Distinct().Where(t => t <= Environment.ProcessorCount).OrderBy(t => t).ToArray();

    for (int i = 0; i < warmup; i++)
        foreach (var q in data.InfixQueries)
            ParallelQueryMatcher.MatchWithinParallel(q, snapshot, 1);

    int? referenceHits = null;
    foreach (int threads in threadCounts)
    {
        ForceGc();
        var (throughput, p50, p95, hits) = MeasureParallel(data.InfixQueries, snapshot, threads, iterations);
        referenceHits ??= hits;
        string label = threads == 1 ? "sequential" : $"T={threads}";
        string status = hits == referenceHits ? "" : " (hit count mismatch)";
        Console.WriteLine($"  {label,-12} {throughput,10:N0} q/s   P50 {p50:F4} ms   P95 {p95:F4} ms{status}");
    }

    Console.WriteLine();
}

void RunQueryBench(string label, IReadOnlyList<string> queries, Func<string, object> run)
{
    for (int i = 0; i < warmup; i++)
        foreach (var q in queries)
            run(q);

    ForceGc();
    var (throughput, p50, p95) = Measure(queries, run, iterations);
    Console.WriteLine($"  {label,-10} {throughput,10:N0} q/s   P50 {p50:F4} ms   P95 {p95:F4} ms");
}

(double throughput, double p50, double p95) Measure(
    IReadOnlyList<string> queries,
    Func<string, object> run,
    int iterationCount)
{
    var batchMs = new List<double>(iterationCount);
    var perQueryMs = new List<double>(iterationCount * queries.Count);

    for (int iter = 0; iter < iterationCount; iter++)
    {
        var batchTimer = Stopwatch.StartNew();
        foreach (var q in queries)
        {
            var qt = Stopwatch.StartNew();
            run(q);
            qt.Stop();
            if (iter > 0)
                perQueryMs.Add(qt.Elapsed.TotalMilliseconds);
        }
        batchTimer.Stop();
        batchMs.Add(batchTimer.Elapsed.TotalMilliseconds);
    }

    double totalSec = batchMs.Sum() / 1000.0;
    double throughput = queries.Count * iterationCount / totalSec;
    var ordered = perQueryMs.OrderBy(x => x).ToArray();
    return (throughput, Percentile(ordered, 0.50), Percentile(ordered, 0.95));
}

(double throughput, double p50, double p95, int hits) MeasureParallel(
    IReadOnlyList<string> queries,
    IndexSnapshot snapshot,
    int threads,
    int iterationCount)
{
    var batchMs = new List<double>(iterationCount);
    var perQueryMs = new List<double>(iterationCount * queries.Count);
    int totalHits = 0;

    for (int iter = 0; iter < iterationCount; iter++)
    {
        var batchTimer = Stopwatch.StartNew();
        int hits = 0;
        foreach (var q in queries)
        {
            var qt = Stopwatch.StartNew();
            hits += ParallelQueryMatcher.MatchWithinParallel(q, snapshot, threads).GetTrueCount();
            qt.Stop();
            if (iter > 0)
                perQueryMs.Add(qt.Elapsed.TotalMilliseconds);
        }
        batchTimer.Stop();
        batchMs.Add(batchTimer.Elapsed.TotalMilliseconds);
        totalHits = hits;
    }

    double totalSec = batchMs.Sum() / 1000.0;
    double throughput = queries.Count * iterationCount / totalSec;
    var ordered = perQueryMs.OrderBy(x => x).ToArray();
    return (throughput, Percentile(ordered, 0.50), Percentile(ordered, 0.95), totalHits / iterationCount);
}

static void ForceGc()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static double Percentile(IReadOnlyList<double> ordered, double p)
{
    if (ordered.Count == 0) return 0;
    double idx = (ordered.Count - 1) * p;
    int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
    return lo == hi ? ordered[lo] : ordered[lo] + (ordered[hi] - ordered[lo]) * (idx - lo);
}

static int GetInt(string[] args, string flag, int defaultValue)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag && int.TryParse(args[i + 1], out int v))
            return v;
    return defaultValue;
}

sealed record BenchScenario(string Name, int DocumentCount, int VocabularySize, int QueryCount);
sealed record BenchDocument(int Id, string Text, string[] Terms);
sealed record BenchData(
    IReadOnlyList<BenchDocument> Documents,
    IReadOnlyList<string> ExactQueries,
    IReadOnlyList<string> InfixQueries,
    IReadOnlyList<string> BooleanQueries);

static class SyntheticDataFactory
{
    private static readonly char[] Vowels = ['a', 'e', 'i', 'o', 'u', 'y'];
    private static readonly char[] Consonants = ['b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'r', 's', 't', 'v', 'w', 'z'];

    public static BenchData Create(BenchScenario scenario, int seed)
    {
        var rng = new Random(seed);
        var vocabulary = BuildVocabulary(scenario.VocabularySize, rng);
        Shuffle(vocabulary, rng);

        int hotCount = Math.Max(64, scenario.VocabularySize / 20);
        var documents = new List<BenchDocument>(scenario.DocumentCount);

        for (int id = 0; id < scenario.DocumentCount; id++)
        {
            var terms = new string[32];
            for (int i = 0; i < terms.Length; i++)
                terms[i] = PickWord(vocabulary, hotCount, rng);
            documents.Add(new BenchDocument(id, string.Join(' ', terms), terms));
        }

        return new BenchData(
            documents,
            BuildExactQueries(documents, scenario.QueryCount, rng),
            BuildInfixQueries(documents, scenario.QueryCount, rng),
            BuildBooleanQueries(documents, scenario.QueryCount, rng));
    }

    private static string[] BuildVocabulary(int size, Random rng)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (set.Count < size)
            set.Add(MakeWord(rng));
        return [.. set];
    }

    private static string MakeWord(Random rng)
    {
        int len = rng.Next(5, 11);
        var sb = new StringBuilder(len);
        bool vowel = rng.Next(2) == 0;
        for (int i = 0; i < len; i++)
        {
            var src = vowel ? Vowels : Consonants;
            sb.Append(src[rng.Next(src.Length)]);
            vowel = !vowel;
        }
        return sb.ToString();
    }

    private static string PickWord(string[] vocab, int hotCount, Random rng)
        => rng.NextDouble() < 0.70 ? vocab[rng.Next(hotCount)] : vocab[rng.Next(vocab.Length)];

    private static IReadOnlyList<string> BuildExactQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            result.Add(doc.Terms[rng.Next(doc.Terms.Length)]);
        }
        return result;
    }

    private static IReadOnlyList<string> BuildInfixQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            var word = doc.Terms[rng.Next(doc.Terms.Length)];
            if (word.Length < 5) continue;
            int start = rng.Next(1, word.Length - 3);
            int maxLen = Math.Min(4, word.Length - start - 1);
            int len = rng.Next(2, maxLen + 1);
            result.Add(word.Substring(start, len));
        }
        return result;
    }

    private static IReadOnlyList<string> BuildBooleanQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            var a = doc.Terms[rng.Next(doc.Terms.Length)];
            var b = doc.Terms[rng.Next(doc.Terms.Length)];
            var c = doc.Terms[rng.Next(doc.Terms.Length)];
            result.Add(rng.Next(3) switch
            {
                0 => $"{a} AND {b}",
                1 => $"{a} OR {b}",
                _ => $"{a} AND NOT {c}"
            });
        }
        return result;
    }

    private static void Shuffle(string[] arr, Random rng)
    {
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }
}
