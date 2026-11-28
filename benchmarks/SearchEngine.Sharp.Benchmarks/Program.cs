using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

// Query throughput on synthetic data.
//
//   dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks
//   dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy

//   dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet

int warmup     = GetInt(args, "--warmup", 3);
int iterations = GetInt(args, "--iterations", 10);
int seed       = GetInt(args, "--seed", 1337);
bool facetBench = args.Contains("--facet");
bool ingestionPolicy = args.Contains("--ingestion-policy");
int ingestionCount = GetInt(args, "--ingestion-count", 100_000);
int ingestionScanDelayMs = GetInt(args, "--ingestion-scan-delay-ms", 0);

if (ingestionPolicy)
{
    IngestionPolicyBenchmark.Run(ingestionCount, seed, ingestionScanDelayMs);
    return;
}

    Console.WriteLine($"Runtime  : {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"Arch     : {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"RID      : {RuntimeInformation.RuntimeIdentifier}");
    Console.WriteLine($"OS       : {RuntimeInformation.OSDescription}");
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

if (facetBench)
{
    Console.WriteLine();
    foreach (var scenario in scenarios.Where(s => s.Name is "medium" or "large"))
        RunFacetScenario(scenario);
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
    RunQueryBench("Glob", data.GlobQueries, q => engine.Find(q, WordMatchMethod.Exact));
    RunQueryBench("Boolean", data.BooleanQueries, q => engine.Find(q, WordMatchMethod.Exact, enableOperators: true));
    Console.WriteLine();
}

void RunFacetScenario(BenchScenario scenario)
{
    Console.WriteLine($"=== facet — {scenario.Name} — {scenario.DocumentCount:N0} docs ===");

    var data = SyntheticDataFactory.CreateWithFacets(scenario, seed);
    var provider = new IndexSnapshotProvider();
    var updater = new IndexUpdater(provider);
    updater.RebuildFrom(data.Documents.ToDictionary(
        d => d.Id,
        d => new IndexedEntry(
            d.Text,
            d.Text,
            FacetValues.FromDictionary(new Dictionary<string, long>
            {
                ["size"] = d.SizeBytes,
                ["modified"] = d.ModifiedTicks,
            }))));
    var engine = new SearchEngineSharp(provider);

    var sizeFilter = FacetFilter.Range("size", 1_024, 1_048_576);
    var recentFilter = FacetFilter.Range("modified", DateTime.UtcNow.AddDays(-30).Ticks, long.MaxValue);
    var combinedFilter = FacetFilter.Combine(sizeFilter, recentFilter);

    RunQueryBench("Exact+Filter", data.ExactQueries, q => engine.Find(q, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, sizeFilter));
    RunQueryBench("Within+Filter", data.InfixQueries, q => engine.Find(q, WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, sizeFilter));
    RunQueryBench("Glob+Filter", data.GlobQueries, q => engine.Find(q, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, combinedFilter));
    RunQueryBench("FilterOnly", data.ExactQueries, _ => engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, sizeFilter));
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
