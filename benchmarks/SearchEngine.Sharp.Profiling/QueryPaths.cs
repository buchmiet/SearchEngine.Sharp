using System.Diagnostics;
using System.Text.Json;
using SearchEngine.Filters;
using SearchEngine.Snapshots;

namespace SearchEngine.Profiling;

/// <summary>Shared helpers: corpus to indexed entries, fixed snapshot provider, latency loop.</summary>
static class Bench
{
    public static Dictionary<int, IndexedEntry> Entries(int n, int seed, bool facets)
    {
        var (_, files) = Corpus.Generate(n, seed);
        var d = new Dictionary<int, IndexedEntry>(n);
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            FacetValues? fv = facets
                ? FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = f.Size,
                    ["modified"] = f.ModifiedTicks,
                    ["attrs"] = f.Name.StartsWith('.') ? (long)(FileAttributes.Hidden | FileAttributes.Normal) : (long)FileAttributes.Normal,
                })
                : null;
            d[i] = new IndexedEntry(f.Name, f.Name, fv);
        }
        return d;
    }

    public sealed class Fixed(IndexSnapshot s) : IIndexSnapshotProvider { public IndexSnapshot Current => s; }

    /// <summary>Median and p95 latency (µs) and allocated bytes per call of <paramref name="f"/>.</summary>
    public static (double MedUs, double P95Us, double AllocB, int Result, int Iters) Measure(Func<int> f, double budgetMs = 300, int minIters = 5, int maxIters = 2000)
    {
        int result = f(); // warm-up
        f();
        var samples = new List<double>();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var total = Stopwatch.StartNew();
        while (samples.Count < minIters || (total.Elapsed.TotalMilliseconds < budgetMs && samples.Count < maxIters))
        {
            long t0 = Stopwatch.GetTimestamp();
            f();
            samples.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
        }
        double alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)samples.Count;
        samples.Sort();
        return (Math.Round(samples[samples.Count / 2], 2), Math.Round(samples[(int)(samples.Count * 0.95)], 2), Math.Round(alloc), result, samples.Count);
    }

    public static readonly long Cutoff30 = CommanderPattern.Cutoff(30);
}

/// <summary>Latency and allocations of every query path against N and selectivity, both presets.</summary>
static class QueryPaths
{
    public sealed record Q(string Name, string Expr, WordMatchMethod Method, bool Ops, FacetFilter? Filter = null, SearchSortMode Sort = SearchSortMode.SnapshotOrder);

    static readonly FacetFilter NotDir = FacetFilter.Mask("attrs", 0, (long)FileAttributes.Directory);

    public static Q[] DefaultQueries =>
    [
        new("exact-dense", "jpg", WordMatchMethod.Exact, false),
        new("exact-mid", "final", WordMatchMethod.Exact, false),
        new("exact-miss", "zzzqqq", WordMatchMethod.Exact, false),
        new("exact-ops-on", "final", WordMatchMethod.Exact, true),
        new("within-2", "re", WordMatchMethod.Within, false),
        new("within-3", "rep", WordMatchMethod.Within, false),
        new("within-6", "report", WordMatchMethod.Within, false),
        new("within-tion", "tion", WordMatchMethod.Within, false),
        new("within-1", "q", WordMatchMethod.Within, false),
        new("glob-prefix", "rep*", WordMatchMethod.Exact, false),
        new("glob-suffix", "*ing", WordMatchMethod.Exact, false),
        new("glob-infix", "*port*", WordMatchMethod.Exact, false),
        new("glob-qmark", "dsc?????", WordMatchMethod.Exact, false),
        new("regex-anch", "^rep.*t$", WordMatchMethod.Regex, false),
        new("regex-digits", @"\d{4}", WordMatchMethod.Regex, false),
        new("and", "report AND pdf", WordMatchMethod.Exact, true),
        new("or", "jpg OR png", WordMatchMethod.Exact, true),
        new("and-not", "report AND NOT final", WordMatchMethod.Exact, true),
        new("complex", "(jpg OR png) AND NOT img", WordMatchMethod.Exact, true),
        new("within-and", "rep AND pdf", WordMatchMethod.Within, true),
        new("exact+facet-sparse", "pdf", WordMatchMethod.Exact, false, FacetFilter.Range("size", 100L << 20, long.MaxValue)),
        new("within+facet", "report", WordMatchMethod.Within, false, FacetFilter.Range("modified", Bench.Cutoff30, long.MaxValue)),
        new("filter-only-dense", "", WordMatchMethod.Exact, false, NotDir),
        new("filter-only-sparse", "", WordMatchMethod.Exact, false, FacetFilter.Range("size", 1L << 30, long.MaxValue)),
        new("natsort-exact-dense", "jpg", WordMatchMethod.Exact, false, null, SearchSortMode.NaturalSortAscending),
        new("natsort-within-sparse", "report", WordMatchMethod.Within, false, null, SearchSortMode.NaturalSortAscending),
    ];

    public static Q[] FileMaskQueries =>
    [
        new("fm-all", "*", WordMatchMethod.Exact, true, NotDir),
        new("fm-suffix", "*.pdf", WordMatchMethod.Exact, true, NotDir),
        new("fm-infix", "*report*", WordMatchMethod.Exact, true, NotDir),
        new("fm-prefix", "img_*", WordMatchMethod.Exact, true, NotDir),
        new("fm-qmark", "dsc?????.jpg", WordMatchMethod.Exact, true, NotDir),
        new("fm-exact", "package.json", WordMatchMethod.Exact, true, NotDir),
        new("fm-or", "*.jpg OR *.png", WordMatchMethod.Exact, true, NotDir),
        new("fm-and-not", "*.cs AND NOT *tests*", WordMatchMethod.Exact, true, NotDir),
        new("fm-regex", @"\.(jpg|png)$", WordMatchMethod.Regex, false, NotDir),
        new("fm-suffix+size", "*.log", WordMatchMethod.Exact, true, FacetFilter.Combine(NotDir, FacetFilter.Range("size", 1 << 20, long.MaxValue))),
        new("fm-all+days", "*", WordMatchMethod.Exact, true, FacetFilter.Combine(NotDir, FacetFilter.Range("modified", Bench.Cutoff30, long.MaxValue))),
        new("fm-within", "report", WordMatchMethod.Within, false, NotDir),
    ];

    public static void Run(Args a)
    {
        int seed = a.Int("seed", 2026);
        foreach (var nStr in a.Str("sizes", "10000,100000,1000000").Split(','))
        {
            int n = int.Parse(nStr);
            var entries = Bench.Entries(n, seed, facets: true);
            foreach (var (presetName, preset, queries) in new[]
                     {
                         ("Default", SearchTokenization.Default, DefaultQueries),
                         ("FileMask", SearchTokenization.FileMask, FileMaskQueries),
                     })
            {
                var sw = Stopwatch.StartNew();
                var snap = IndexSnapshotBuilder.Build(entries, preset);
                double buildMs = sw.Elapsed.TotalMilliseconds;
                var engine = new SearchEngineSharp(new Bench.Fixed(snap));
                foreach (var q in queries)
                {
                    if (q.Sort == SearchSortMode.NaturalSortAscending)
                    {
                        // Cold: first natural-sort query on a fresh snapshot (one sample).
                        var fresh = new SearchEngineSharp(new Bench.Fixed(IndexSnapshotBuilder.Build(entries, preset)));
                        var t0 = Stopwatch.GetTimestamp();
                        int c = fresh.Find(q.Expr, q.Method, q.Ops, q.Sort, q.Filter).Count;
                        Emit(a, n, presetName, q.Name + "-cold", Stopwatch.GetElapsedTime(t0).TotalMicroseconds, 0, 0, c, 1, buildMs, snap);
                    }
                    var m = Bench.Measure(() => engine.Find(q.Expr, q.Method, q.Ops, q.Sort, q.Filter).Count);
                    Emit(a, n, presetName, q.Name, m.MedUs, m.P95Us, m.AllocB, m.Result, m.Iters, buildMs, snap);
                }
            }
        }
    }

    static void Emit(Args a, int n, string preset, string q, double med, double p95, double alloc, int hits, int iters, double buildMs, IndexSnapshot s)
        => Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "querypaths", label = a.Opt("label"), n, preset, q, medUs = med, p95Us = p95, allocB = alloc, hits, iters,
            buildMs = Math.Round(buildMs, 1), words = s.UniqueWordCount,
            tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
        }));
}
