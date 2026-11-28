using System.Diagnostics;
using System.Text.Json;
using SearchEngine.Profiling;
using ThreeWay;

if (args.Contains("lifecycle")) return ThreeWay.Lifecycle.Run(args);
if (args.Contains("sustained")) return ThreeWay.Sustained.Run(args);

string engineName = "sharp"; int n = 100_000; int seed = 2026; int builds = 3; string? outPath = null; string tmp = Path.Combine(Path.GetTempPath(), "threeway");
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--engine": engineName = args[++i]; break;
        case "--n": n = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--builds": builds = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--tmp": tmp = args[++i]; break;
    }
}

var (_, files) = Corpus.Generate(n, seed);
var names = files.Select(f => f.Name).ToArray();
using IEngine eng = engineName switch { "sharp" => new SharpEngine(), "lucene" => new LuceneEngine(), "tantivy" => new TantivyEngine(), _ => throw new ArgumentException(engineName) };
Console.Error.WriteLine($"# {eng.Name}  n={n:N0}  seed={seed}  cpu-threads={Environment.ProcessorCount}  .NET {Environment.Version}");

var rows = new List<Dictionary<string, object?>>();
void Row(string kind, string id, string desc, Dictionary<string, object?> v)
{
    var d = new Dictionary<string, object?> { ["engine"] = eng.Name, ["n"] = n, ["kind"] = kind, ["id"] = id, ["desc"] = desc };
    foreach (var kv in v) d[kv.Key] = kv.Value;
    rows.Add(d);
}

// ---- build: first build measured for memory, then repeats for timing
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
long rss0 = Proc.RssBytes(); long heap0 = GC.GetTotalMemory(true);
var sw = Stopwatch.StartNew();
eng.Build(names);
double first = sw.Elapsed.TotalSeconds;
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
long rss1 = Proc.RssBytes(); long heap1 = GC.GetTotalMemory(true);
var times = new List<double> { first };
for (int b = 1; b < builds; b++) { sw.Restart(); eng.Build(names); times.Add(sw.Elapsed.TotalSeconds); }
times.Sort();
Row("build", "build_inmem", "index build, 1 thread, in-memory index (lucene/tantivy: add+commit)", new() { ["sec_median"] = times[times.Count / 2], ["sec_first"] = first, ["sec_min"] = times[0], ["rss_delta_mb"] = (rss1 - rss0) / 1048576.0, ["managed_heap_delta_mb"] = (heap1 - heap0) / 1048576.0 });
Console.Error.WriteLine($"build: median {times[times.Count / 2]:F3}s (first {first:F3}s)  rss+{(rss1 - rss0) / 1048576.0:F0}MB  heap+{(heap1 - heap0) / 1048576.0:F0}MB");

// ---- queries (before any merge: as-built), then after merge-to-1-segment
void RunQueries(string phase)
{
    foreach (var q in Queries.All)
    {
        if (!eng.Supports(q)) { Row("query", q.Id, q.Description, new() { ["phase"] = phase, ["supported"] = false }); continue; }
        long hits = 0;
        try { hits = eng.Count(q); } catch (Exception e) { Console.Error.WriteLine($"{q.Id}: {e.Message}"); continue; }
        var count = Timing.Measure(() => eng.Count(q));
        var top = Timing.Measure(() => eng.Top10(q));
        // all-ids materialisation: huge result sets get a smaller time budget
        var all = Timing.Measure(() => eng.All(q), budgetMs: hits > 100_000 ? 1500 : 700, minSamples: hits > 100_000 ? 5 : 15);
        Timing.Result? alt = eng.AllAlt(q) is null ? null : Timing.Measure(() => eng.AllAlt(q)!.Value, budgetMs: hits > 100_000 ? 1500 : 700, minSamples: hits > 100_000 ? 5 : 15);
        Row("query", q.Id, q.Description, new()
        {
            ["all_stored_us"] = alt?.MedianUs,
            ["phase"] = phase, ["supported"] = true, ["hits"] = hits,
            ["count_us"] = count.MedianUs, ["count_p95_us"] = count.P95Us,
            ["top10_us"] = top.MedianUs, ["top10_p95_us"] = top.P95Us,
            ["all_us"] = all.MedianUs, ["all_p95_us"] = all.P95Us,
        });
        Console.Error.WriteLine($"[{phase}] {q.Id,-12} hits={hits,9:N0}  count {count.MedianUs,10:F1}us  top10 {top.MedianUs,10:F1}us  all {all.MedianUs,11:F1}us");
    }
}
RunQueries("as_built");
sw.Restart(); eng.ForceMerge(); double merge = sw.Elapsed.TotalSeconds;
Row("build", "force_merge", "merge to one segment", new() { ["sec"] = merge });
Console.Error.WriteLine($"force merge: {merge:F3}s");
RunQueries("merged");

// ---- single-document update: add one doc, then query sees it
{
    var lat = new List<double>();
    for (int i = 0; i < 25; i++)
    {
        string tok = $"zqtok{i}x"; long t0 = Stopwatch.GetTimestamp();
        long found = eng.AddOneAndSearch(n + 1 + i, $"{tok} fresh file {i}.txt", tok);
        lat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
        if (found != 1) Console.Error.WriteLine($"!! update visibility: expected 1 hit got {found}");
    }
    var first5 = lat.Take(5).Average();
    lat.Sort();
    Row("update", "add_one_visible", "add 1 document, then a query sees it", new() { ["median_us"] = lat[lat.Count / 2], ["p95_us"] = lat[(int)(lat.Count * 0.95)], ["min_us"] = lat[0] });
    Console.Error.WriteLine($"add-one-and-visible: median {lat[lat.Count / 2] / 1000.0:F2} ms  p95 {lat[(int)(lat.Count * 0.95)] / 1000.0:F2} ms");
}

// ---- persistent build (engines that have one)
{
    string dir = Path.Combine(tmp, $"{engineName}-{n}");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    var d = eng.DiskBuild(names, dir);
    if (d is { } r)
    {
        Row("disk", "disk_build", "build into a directory on disk, 1 thread", new() { ["build_sec"] = r.BuildSec, ["bytes"] = r.Bytes, ["cold_open_first_query_sec"] = r.OpenSec });
        Console.Error.WriteLine($"disk: build {r.BuildSec:F2}s  {r.Bytes / 1048576.0:F1} MiB  open+first query {r.OpenSec * 1000:F1} ms");
        Directory.Delete(dir, true);
    }
}

if (outPath is not null) File.WriteAllText(outPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
return 0;
