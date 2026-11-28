// Write-path latency of IndexUpdater: how long one AddEntry / RefreshEntry / RemoveEntry call takes while the index
// grows, how much of the total time the slowest calls take, and what a reader sees while a writer runs.
using System.Diagnostics;
using SearchEngine;
using SearchEngine.Profiling;

int n = 1_000_000, ops = 20_000, seed = 2026, readers = 1; string mix = "add"; int rate = 0; int fanout = 4; double ratio = 2.0;
for (int i = 0; i < args.Length; i++)
    switch (args[i]) { case "--n": n = int.Parse(args[++i]); break; case "--ops": ops = int.Parse(args[++i]); break; case "--mix": mix = args[++i]; break; case "--readers": readers = int.Parse(args[++i]); break; case "--rate": rate = int.Parse(args[++i]); break; case "--fanout": fanout = int.Parse(args[++i]); break; case "--ratio": ratio = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break; }

var (_, files) = Corpus.Generate(n + ops, seed);
var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider, null, new MergePolicy { FanOut = fanout, SimilarFactor = ratio });
var engine = new SearchEngineSharp(provider);
var initial = new Dictionary<int, string>(n);
for (int i = 0; i < n; i++) initial[i] = files[i].Name;
var sw = Stopwatch.StartNew();
updater.RebuildFrom(initial);
Console.WriteLine($"initial build of {n:N0}: {sw.Elapsed.TotalSeconds:F2}s");
engine.Find("final", WordMatchMethod.Exact); engine.Find("rep", WordMatchMethod.Within);
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); // the index just built sits in a young generation; collect it as a long-lived server would have

// Reader threads: query latency while the writer works.
var stop = false; var readerSamples = new List<double>[readers];
var threads = new Thread[readers];
for (int r = 0; r < readers; r++)
{
    int idx = r; readerSamples[idx] = new();
    threads[idx] = new Thread(() =>
    {
        string[] qs = ["final", "report AND pdf", "jpg OR png"];
        int k = 0;
        while (!Volatile.Read(ref stop))
        {
            long a = Stopwatch.GetTimestamp();
            engine.Find(qs[k++ % qs.Length], WordMatchMethod.Exact, true);
            readerSamples[idx].Add((Stopwatch.GetTimestamp() - a) * 1e6 / Stopwatch.Frequency);
            Thread.Sleep(0);
        }
    }) { IsBackground = true };
}
foreach (var t in threads) t.Start();

var rng = new Random(7); long segSum = 0; int segSamples = 0;
var lat = new double[ops];
var gcAtOp = new (int Gen0, int Gen2)[ops];
long pause0 = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
int nextId = n;
long t0 = Stopwatch.GetTimestamp();
for (int i = 0; i < ops; i++)
{
    if (rate > 0) { long due = t0 + (long)(i * (double)Stopwatch.Frequency / rate); while (Stopwatch.GetTimestamp() < due) Thread.Sleep(0); }
    long a = Stopwatch.GetTimestamp();
    int kind = mix == "add" ? 0 : rng.Next(10) switch { < 6 => 0, < 9 => 1, _ => 2 };
    switch (kind)
    {
        case 0: updater.AddEntry(nextId, files[nextId].Name); nextId++; break;
        case 1: { int id = rng.Next(n); updater.RefreshEntry(id, files[id].Name + "_v2"); break; }
        default: updater.RemoveEntry(rng.Next(n)); break;
    }
    lat[i] = (Stopwatch.GetTimestamp() - a) * 1e6 / Stopwatch.Frequency;
    gcAtOp[i] = (GC.CollectionCount(0), GC.CollectionCount(2));
    if (i % 50 == 0) { segSum += updater.SegmentCount; segSamples++; }
}
double total = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
Volatile.Write(ref stop, true); foreach (var t in threads) t.Join();

var sorted = (double[])lat.Clone(); Array.Sort(sorted);
double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
double top1pct = sorted.Skip((int)(sorted.Length * 0.99)).Sum() / lat.Sum() * 100;
Console.WriteLine($"rate={(rate == 0 ? "max" : rate + "/s")}  mix={mix}  n={n:N0}  ops={ops:N0}  total {total:F2}s  ({ops / total:N0} ops/s)");
Console.WriteLine($"write latency µs: p50 {P(.5):F0}  p90 {P(.9):F0}  p99 {P(.99):F0}  p99.9 {P(.999):F0}  max {sorted[^1]:F0}   slowest 1% of calls = {top1pct:F0}% of write time");
Console.WriteLine($"calls > 10 ms: {lat.Count(x => x > 10_000)}   > 100 ms: {lat.Count(x => x > 100_000)}   > 1 s: {lat.Count(x => x > 1_000_000)}");
Console.WriteLine($"GC during writes: gen0 {gcAtOp[^1].Gen0 - gcAtOp[0].Gen0}, gen2 {gcAtOp[^1].Gen2 - gcAtOp[0].Gen2}, total pause {(long)GC.GetTotalPauseDuration().TotalMilliseconds - pause0} ms");
foreach (int i in Enumerable.Range(1, ops - 1).Where(i => lat[i] > 10_000).OrderByDescending(i => lat[i]).Take(6))
    Console.WriteLine($"  slow op #{i}: {lat[i] / 1000:F1} ms   gen2 collections during it: {gcAtOp[i].Gen2 - gcAtOp[i - 1].Gen2}, gen0: {gcAtOp[i].Gen0 - gcAtOp[i - 1].Gen0}");
Console.WriteLine($"merges: {updater.Statistics.BackgroundMerges} background, {updater.Statistics.SynchronousMerges} in the write path; {updater.Statistics.DocumentsWritten:N0} documents written by merges for {nextId - n:N0} added ({updater.Statistics.DocumentsWritten / (double)Math.Max(1, nextId - n):F1}x); merge time {updater.Statistics.MergeTime.TotalSeconds:F2}s");
Console.WriteLine($"segments: average {segSum / (double)segSamples:F1} while writing, {updater.SegmentCount} at the end");
updater.WaitForMerges();
Console.WriteLine($"segments after the merges settle: {updater.SegmentCount}");
foreach (var rs in readerSamples)
{
    var s = rs.OrderBy(x => x).ToArray();
    if (s.Length > 0) Console.WriteLine($"reader: {s.Length:N0} queries  p50 {s[s.Length / 2]:F0} µs  p99 {s[(int)(s.Length * 0.99)]:F0} µs  max {s[^1]:F0} µs");
}
sw.Restart();
for (int i = 0; i < 200; i++) engine.Find("final", WordMatchMethod.Exact);
Console.WriteLine($"query 'final' after writes: {sw.Elapsed.TotalMilliseconds * 1000 / 200:F1} µs avg; docs {engine.DocumentCount:N0}");
